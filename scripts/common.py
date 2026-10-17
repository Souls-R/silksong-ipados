"""Small shared helpers; all generated files stay outside the source tree."""
from pathlib import Path, PurePosixPath
import json
import subprocess

REPO = Path(__file__).resolve().parents[1]
UNITY_VERSION = '6000.0.50f1'
PACK_ID = 'silksong-ios-resources-v1'
PACK_FOLDER = 'SilksongResources-v1'
RESOURCE_REVISION = 2


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.tmp')
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding='utf-8')
    temporary.replace(path)


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def relative_bundle(name):
    if (not isinstance(name, str) or not name or '\\' in name or ':' in name
            or '\0' in name or any(p in ('', '.', '..') for p in name.split('/'))
            or not name.endswith('.bundle')):
        raise ValueError(f'Invalid bundle path: {name!r}')
    return PurePosixPath(name)


def output_directory(path, *inputs):
    path = path.expanduser().resolve()
    # A generated tree must never cover or sit inside source code/game/tools.
    for source in (REPO, *inputs):
        source = source.expanduser().resolve()
        if path.is_relative_to(source) or source.is_relative_to(path):
            raise ValueError(f'Output overlaps an input/source directory: {path}')
    return path


def require_files(root, names):
    missing = [name for name in names if not (root / name).is_file()]
    if missing:
        raise ValueError(f'Missing files in {root}: ' + ', '.join(missing))


def run(command, log, cwd=None):
    log.parent.mkdir(parents=True, exist_ok=True)
    print(f'Running {Path(str(command[0])).name}; log: {log}', flush=True)
    with log.open('w', encoding='utf-8') as stream:
        result = subprocess.run([str(x) for x in command], cwd=cwd,
                                stdout=stream, stderr=subprocess.STDOUT)
    if result.returncode:
        tail = log.read_text(encoding='utf-8', errors='replace')[-6000:]
        raise RuntimeError(f'Command failed ({result.returncode}):\n{tail}')
