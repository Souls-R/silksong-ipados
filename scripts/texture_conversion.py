"""Resumable, scene-independent Mac bundle -> iOS resource conversion.

Decode every original mip; never regenerate mips or change sprite/atlas objects.
ASTC profiles follow Unity's TextureColorSpace (Linear=0, sRGB=1) and Arm's
encoder API. See UnityCsReference/Runtime/Export/Graphics/GraphicsEnums.cs and
https://github.com/ARM-software/astc-encoder/blob/main/Docs/Encoding.md.
This is offline validation, not an iOS runtime compatibility claim.
"""
import gc, importlib.metadata, json, math
import time, warnings
from functools import lru_cache

import UnityPy
from UnityPy.export import Texture2DConverter as converter
from UnityPy.streams import EndianBinaryReader
import astc_encoder as astc
import texture2ddecoder
from PIL import Image, ImageChops
from .common import RESOURCE_REVISION

warnings.simplefilter('ignore')
UnityPy.config.FALLBACK_UNITY_VERSION = '6000.0.50f1'
POLICY = dict(revision=RESOURCE_REVISION, unity='6000.0.50f1', platform=9, block=4,
              quality=10, min_pixels=4096, premultiplied_psnr_min=32,
              alpha_psnr_min=36, alpha_p99_max=20,
              UnityPy=importlib.metadata.version('UnityPy'),
              astc_encoder=importlib.metadata.version('astc-encoder-py'))

def texture_payload(tex):
    if not tex.image_data and (tex.m_StreamData is None or not tex.m_StreamData.size):
        return b''
    return tex.get_image_data()


def atomic_json(path, value):
    temp = path.with_suffix(path.suffix + '.tmp')
    temp.write_text(json.dumps(value, indent=2), encoding='utf-8')
    temp.replace(path)


def mip_bytes(fmt, width, height):
    if fmt in (1, 3, 4):
        return width * height * {1: 1, 3: 3, 4: 4}[fmt]
    if fmt in (10, 12, 25, 54):
        return ((width + 3)//4) * ((height + 3)//4) * (8 if fmt == 10 else 16)
    raise ValueError(f'Unsupported source format {fmt}; explicit handling required')


def original_mips(tex):
    assert tex.m_ImageCount == 1 and tex.m_TextureDimension == 2
    assert not tex.m_MipsStripped, 'Stripped mip chains need explicit handling'
    raw = tex.get_image_data()
    fmt = tex.m_TextureFormat
    if fmt == 28:
        raw = texture2ddecoder.unpack_unity_crunch(raw)
        fmt = 10
    width, height = tex.m_Width, tex.m_Height
    offset = 0
    for _ in range(max(1, tex.m_MipCount or 1)):
        count = mip_bytes(fmt, width, height)
        assert offset + count <= len(raw), 'Truncated mip chain'
        image = converter.parse_image_data(raw[offset:offset+count], width, height,
                    fmt, tex.object_reader.version, tex.object_reader.platform,
                    tex.m_PlatformBlob, flip=False).convert('RGBA')
        yield image
        offset += count
        width, height = max(1, width//2), max(1, height//2)
    assert offset == len(raw), f'Unexpected trailing mip bytes: {len(raw)-offset}'


@lru_cache(maxsize=4)
def context(srgb):
    profile = astc.ASTCProfile.LDR_SRGB if srgb else astc.ASTCProfile.LDR
    return astc.ASTCContext(astc.ASTCConfig(profile,4,4,quality=POLICY['quality']),threads=1)


def psnr(histogram):
    count = sum(histogram)
    mse = sum(i*i*n for i,n in enumerate(histogram))/max(1,count)
    return 99.0 if not mse else round(10*math.log10(255*255/mse),3)


def error_metrics(before, after):
    # Full-pixel histograms, including alpha edges. Invisible RGB is excluded
    # from the color score by premultiplication; alpha has a separate gate.
    b, a = before.split(), after.split()
    alpha_hist = ImageChops.difference(b[3],a[3]).histogram()
    rgb_hist = [0]*256
    for i in range(3):
        h = ImageChops.difference(ImageChops.multiply(b[i],b[3]),
                                 ImageChops.multiply(a[i],a[3])).histogram()
        rgb_hist = [x+y for x,y in zip(rgb_hist,h)]
    threshold = sum(alpha_hist)*.99
    total = 0
    for p99,n in enumerate(alpha_hist):
        total += n
        if total >= threshold: break
    return dict(premultiplied_psnr=psnr(rgb_hist),alpha_psnr=psnr(alpha_hist),
                alpha_p99=p99,alpha_max=max(i for i,n in enumerate(alpha_hist) if n))


def acceptable(metrics):
    return (metrics['premultiplied_psnr'] >= POLICY['premultiplied_psnr_min']
            and metrics['alpha_psnr'] >= POLICY['alpha_psnr_min']
            and metrics['alpha_p99'] <= POLICY['alpha_p99_max'])


def convert_texture(tex):
    before = tex.m_TextureFormat
    # Small/mobile-native and single-channel data stay exact. Readable textures
    # may be accessed by CPU game code, so retain their uncompressed layout.
    desktop = before in (10,12,25,28)
    if not desktop and (before == 1 or tex.m_IsReadable or
                        tex.m_Width*tex.m_Height < POLICY['min_pixels']):
        return None
    if before not in (1,3,4,10,12,25,28):
        raise ValueError(f'Unreviewed texture format {before}: {tex.m_Name}')
    if before == 1: return None
    assert tex.m_ColorSpace in (0,1)
    ctx = context(tex.m_ColorSpace == 1)
    cpu_readable = desktop and tex.m_IsReadable
    swizzle = astc.ASTCSwizzle.from_str('RGBA')
    encoded = bytearray()
    scores = []
    originals = []
    started = time.monotonic()
    for mip, image in enumerate(original_mips(tex)):
        originals.append(image.tobytes())
        if cpu_readable:
            scores.append(dict(mip=mip,width=image.width,height=image.height,lossless=True))
            continue
        packed = ctx.compress(astc.ASTCImage(astc.ASTCType.U8,*image.size,1,originals[-1]),swizzle)
        assert len(packed) == mip_bytes(54,*image.size)
        decoded = converter.parse_image_data(packed,*image.size,54,
                    tex.object_reader.version,9,flip=False).convert('RGBA')
        metrics = error_metrics(image,decoded)
        scores.append(dict(mip=mip,width=image.width,height=image.height,**metrics))
        encoded.extend(packed)
    good = not cpu_readable and all(acceptable(score) for score in scores)
    if not good and not desktop:
        return dict(path_id=tex.object_reader.path_id,name=tex.m_Name,from_format=before,
                    to_format=before,decision='preserved: ASTC quality gate',mips=scores)
    # Desktop formats cannot be retained on this route; exact RGBA is a bounded
    # fallback for textures which fail the measured ASTC quality gate.
    target = 54 if good else 4
    payload = bytes(encoded) if good else b''.join(originals)
    tex.image_data = payload
    tex.m_TextureFormat = target
    tex.m_CompleteImageSize = len(payload)
    if tex.m_StreamData is not None:
        tex.m_StreamData.path = ''
        tex.m_StreamData.offset = 0
        tex.m_StreamData.size = 0
    tex.save()
    return dict(path_id=tex.object_reader.path_id,name=tex.m_Name,from_format=before,
                to_format=target,decision='ASTC' if good else ('exact RGBA: CPU-readable' if cpu_readable else 'exact RGBA fallback'),
                output_bytes=len(payload),
                elapsed_seconds=round(time.monotonic()-started,3),mips=scores)


def texture_identity(tex):
    return dict(name=tex.m_Name,width=tex.m_Width,height=tex.m_Height,
                mips=tex.m_MipCount,color_space=tex.m_ColorSpace,
                settings={key:getattr(tex.m_TextureSettings,key) for key in
                          ('m_FilterMode','m_Aniso','m_MipBias','m_WrapU','m_WrapV','m_WrapW')},
                readable=tex.m_IsReadable,dimension=tex.m_TextureDimension,
                streaming=tex.m_StreamingMipmaps,mips_stripped=tex.m_MipsStripped)


def compact_texture_streams(bundle, textures, original_streams):
    # Repack a texture-only resource stream when every non-padding byte is
    # accounted for. Bundles containing other streaming classes stay intact.
    unsafe = {'Mesh','AudioClip','VideoClip','Texture3D','Texture2DArray','Cubemap','CubemapArray'}
    if any(o.type.name in unsafe for af in bundle.files.values()
           if hasattr(af,'objects') for o in af.objects.values()): return []
    groups = {}
    for key, info in original_streams.items():
        if info['path']:
            groups.setdefault(info['path'].rsplit('/',1)[-1],[]).append((key,info))
    compacted = []
    for name, entries in groups.items():
        reader = bundle.files.get(name)
        if reader is None or not hasattr(reader,'bytes'): continue
        raw = reader.bytes
        end = 0
        for key, info in sorted(entries,key=lambda item:item[1]['offset']):
            offset, size = info['offset'], info['size']
            if offset < end or offset > len(raw) or offset-end > 128 or any(raw[end:offset]): break
            end = offset+size
        else:
            if end > len(raw) or len(raw)-end > 128 or any(raw[end:]): continue
            output = bytearray()
            for key, info in entries:
                tex = textures[key]
                data = tex.get_image_data()
                output.extend(b'\0' * (-len(output)%16))
                tex.m_StreamData.path = info['path']
                tex.m_StreamData.offset = len(output)
                tex.m_StreamData.size = len(data)
                tex.image_data = b''
                output.extend(data)
                tex.save()
            replacement = EndianBinaryReader(bytes(output))
            replacement.flags = reader.flags
            bundle.files[name] = replacement
            compacted.append(dict(resource=name,before_bytes=len(raw),after_bytes=len(output)))
    return compacted


def convert_bundle(source, output, manifest, refresh=False):
    """Resume local conversion by file metadata; never hash the whole pack."""
    stat = source.stat()
    identity = dict(path=str(source.resolve()), bytes=stat.st_size, mtime_ns=stat.st_mtime_ns)
    if not refresh and output.is_file() and manifest.is_file():
        try:
            previous = json.loads(manifest.read_text(encoding='utf-8'))
            current = output.stat()
            if (previous['source'] == identity and previous['policy'] == POLICY
                    and previous['bytes'] == current.st_size
                    and previous['output_mtime_ns'] == current.st_mtime_ns):
                return dict(bytes=current.st_size, reused=True)
        except (OSError, ValueError, KeyError):
            pass
    output.parent.mkdir(parents=True, exist_ok=True)
    manifest.parent.mkdir(parents=True, exist_ok=True)
    env = UnityPy.load(str(source))
    bundle, = env.files.values()
    textures, unchanged, identities, original_streams = {}, {}, {}, {}
    for afname, af in bundle.files.items():
        if not hasattr(af, 'objects'):
            continue
        for obj in af.objects.values():
            key = (afname, obj.path_id)
            if obj.type.name != 'Texture2D':
                unchanged[key] = obj.get_raw_data()
            else:
                tex = obj.read()
                textures[key] = tex
                identities[key] = texture_identity(tex)
                original_streams[key] = ({k: getattr(tex.m_StreamData, k)
                                         for k in ('path', 'offset', 'size')}
                                        if tex.m_StreamData else dict(path=''))
        af.unity_version = POLICY['unity']
        af._m_target_platform = POLICY['platform']
        af.mark_changed()
    changes = [row for tex in textures.values() if (row := convert_texture(tex)) is not None]
    pixels = {key: texture_payload(tex) for key, tex in textures.items()}
    formats = {key: tex.m_TextureFormat for key, tex in textures.items()}
    compact_texture_streams(bundle, textures, original_streams)
    temporary = output.with_name(output.name + '.partial')
    temporary.write_bytes(bundle.save(packer='lz4'))
    # Verify the conversion did not alter animation/game objects or texture
    # sampling. Compare the in-memory serialization; no extra content hashes.
    check = UnityPy.load(temporary.read_bytes())
    checked_bundle, = check.files.values()
    objects = {}
    for name, af in checked_bundle.files.items():
        if not hasattr(af, 'objects'):
            continue
        assert af._m_target_platform == POLICY['platform'] and af.unity_version == POLICY['unity']
        objects.update({(name, o.path_id): o for o in af.objects.values()})
    assert set(objects) == set(unchanged) | set(textures), 'Object identities changed'
    for key, value in unchanged.items():
        assert objects[key].get_raw_data() == value, 'Non-texture object changed'
    for key in textures:
        tex = objects[key].read()
        assert texture_identity(tex) == identities[key], 'Texture sampling changed'
        assert tex.m_TextureFormat == formats[key]
        assert texture_payload(tex) == pixels[key], 'Texture serialization changed'
        assert tex.m_TextureFormat not in (10, 12, 25, 28), 'Desktop format remains'
    temporary.replace(output)
    current = output.stat()
    atomic_json(manifest, dict(source=identity, policy=POLICY, bytes=current.st_size,
                             output_mtime_ns=current.st_mtime_ns, textures=changes))
    del env, bundle, check, textures, unchanged, pixels, objects
    gc.collect()
    return dict(bytes=current.st_size, reused=False)
