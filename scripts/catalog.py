"""Read original Addressables 2.5 binary catalog without starting Unity.

Layout verified against Unity's official com.unity.addressables 2.5.0 package,
Runtime/ResourceManager/Util/BinaryStorageBuffer.cs and ResourceLocators/ContentCatalogData.cs.
"""
from pathlib import Path
import struct, json

class Catalog:
    def __init__(self, path):
        self.data = path if isinstance(path, bytes) else Path(path).read_bytes()
        self.header = self.values(0, 8)
        assert self.header[0] == 0x0de38942 and self.header[1] == 2
        self.keys = {}
        for offset in range(self.header[2], self.header[2] + self.values(self.header[2]-4)[0], 8):
            name, locations = self.values(offset, 2)
            type_id, object_id = self.values(name, 2)
            assembly_id, class_id = self.values(type_id, 2)
            if self.string(class_id, '.') != 'System.String': continue
            string_id = self.values(object_id)[0]
            separator = chr(struct.unpack_from('<H', self.data, object_id+4)[0])
            self.keys[self.string(string_id, separator)] = self.array(locations)
    def values(self, offset, count=1):
        assert 0 <= offset <= len(self.data)-4*count
        return struct.unpack_from('<'+'I'*count, self.data, offset)
    def array(self, offset):
        return [] if offset == 0xffffffff else list(self.values(offset, self.values(offset-4)[0]//4))
    def string(self, offset, sep='\0'):
        if offset == 0xffffffff: return None
        if sep != '\0' and offset & 0x40000000:
            parts = []
            while offset != 0xffffffff:
                value, offset = self.values(offset & 0x3fffffff, 2)
                parts.append(self.string(value))
            return sep.join(reversed(parts))
        raw = offset & 0x3fffffff
        return self.data[raw:raw+self.values(raw-4)[0]].decode('utf-16-le' if offset & 0x80000000 else 'ascii')
    def location(self, offset):
        key, internal, provider, deps, dep_hash, extra, typ = self.values(offset, 7)
        assembly, cls = self.values(typ, 2)
        return {'key': self.string(key, '/'), 'internal_id': self.string(internal, '/'), 'provider': self.string(provider, '.'), 'type': self.string(cls, '.'), 'dependencies': self.array(deps)}
    def closure(self, key):
        seen = set(); result = []
        def visit(offset):
            if offset in seen: return
            seen.add(offset)
            loc = self.location(offset)
            result.append(loc)
            for dep in loc['dependencies']: visit(dep)
        for offset in self.keys[key]: visit(offset)
        return result
