import copy
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile
import xml.etree.ElementTree as ET

import preflight as gate


class AdmissionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.m = {'schema': 1, 'lane': 'shipping', 'version': '3.1.2', 'files': []}
        self.spec = self.root / 'package.nuspec'
        self.log = self.put('execution.log', b'synthetic fixture, not runtime evidence')
        parser = self.root / 'parser.nupkg'
        with zipfile.ZipFile(parser, 'w') as z:
            z.writestr('AAXClean.nuspec', '<package><metadata><id>AAXClean</id><version>3.1.1-local.test</version></metadata></package>')
        self.m['parser'] = {**self.put('parser.nupkg', parser.read_bytes()), 'version': '3.1.1-local.test'}
        for tfm in ('net8.0', 'net10.0'):
            for dll in ('AAXClean.Codecs', 'NAudio.Lame'):
                target = f'lib/{tfm}/{dll}.dll'
                self.m['files'].append({**self.put(target, target.encode()), 'target': target})
        for rid in sorted(gate.RIDS):
            self.add_native(rid)
        self.write_spec()

    def put(self, path, data):
        dest = self.root / path
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(data)
        return {'path': path, 'sha256': gate.digest(data)}

    def add_native(self, rid):
        data = bytearray(128)
        arm = rid.endswith('arm64')
        if rid.startswith('linux'):
            data[:6] = b'\x7fELF\x02\x01'
            struct.pack_into('<H', data, 18, 183 if arm else 62)
        elif rid.startswith('osx'):
            data[:4] = b'\xcf\xfa\xed\xfe'
            struct.pack_into('<I', data, 4, 0x100000c if arm else 0x1000007)
        else:
            data[:2] = b'MZ'
            struct.pack_into('<I', data, 60, 64)
            data[64:68] = b'PE\0\0'
            struct.pack_into('<H', data, 68, 0xaa64 if arm else 0x8664)
        filename = 'aaxcleannative.dll' if rid.startswith('win') else 'libaaxcleannative.' + ('dylib' if rid.startswith('osx') else 'so')
        target = f'runtimes/{rid}/native/{filename}'
        record = {**self.put(target, data), 'target': target}
        receipt = {'rid': rid, 'native_sha256': record['sha256'],
                   'managed': {f['target']: f['sha256'] for f in self.m['files'] if f['target'].endswith('.dll') and '/native/' not in f['target']},
                   'parser_sha256': self.m['parser']['sha256'], 'api_version': 2,
                   'exports': sorted(gate.EXPORTS), 'execution_host_rid': rid,
                   'drain_pass': True, 'error_pass': True, 'encoder_timing_pass': True, 'input_format_pass': True, 'source': 'fixture source hash',
                   'dependencies': 'fixture dependency hashes', 'configuration': 'fixture flags',
                   'toolchain': 'fixture toolchain', 'execution_log': self.log,
                   'distribution': 'approved', 'distribution_record': self.log}
        record['receipt'] = self.put(rid + '.json', json.dumps(receipt).encode())
        self.m['files'].append(record)

    def change_receipt(self, **updates):
        record = self.m['files'][-1]
        receipt = json.loads((self.root / record['receipt']['path']).read_text())
        receipt.update(updates)
        record['receipt'] = self.put(record['receipt']['path'], json.dumps(receipt).encode())

    def write_spec(self):
        spec = ET.Element('package')
        meta = ET.SubElement(spec, 'metadata')
        ET.SubElement(meta, 'id').text = 'AAXClean.Codecs'
        ET.SubElement(meta, 'version').text = self.m['version']
        deps = ET.SubElement(meta, 'dependencies')
        for tfm in ('net8.0', 'net10.0'):
            group = ET.SubElement(deps, 'group', targetFramework=tfm)
            ET.SubElement(group, 'dependency', id='AAXClean', version='[' + self.m['parser']['version'] + ']')
        files = ET.SubElement(spec, 'files')
        for f in self.m['files']:
            ET.SubElement(files, 'file', src=f['path'], target=f['target'])
        ET.ElementTree(spec).write(self.spec)

    def check(self, archive=None):
        gate.validate(self.root, self.m, self.spec, self.m['version'], archive)

    def rejects(self, message):
        with self.assertRaisesRegex(ValueError, message):
            self.check()

    def test_shipping_receipts(self):
        self.check()

    def test_missing_encoder_timing_export(self):
        self.change_receipt(exports=sorted(gate.EXPORTS - {'AacEncoder_GetTiming'}))
        self.rejects('missing v2 exports/version')

    def test_missing_encoder_timing_execution(self):
        self.change_receipt(encoder_timing_pass=False)
        self.rejects('encoder timing execution')

    def test_missing_decoded_input_format_export(self):
        self.change_receipt(exports=sorted(gate.EXPORTS - {'Decoder_GetInputFormat'}))
        self.rejects('missing v2 exports/version')

    def test_missing_decoded_input_format_execution(self):
        self.change_receipt(input_format_pass=False)
        self.rejects('decoded input-format execution')

    def test_missing_rid(self):
        self.m['files'].pop()
        self.rejects('RID set')

    def test_duplicate_rid(self):
        f = copy.deepcopy(self.m['files'][-1])
        f['target'] += '.other'
        self.m['files'].append(f)
        self.rejects('unexpected native filename')

    def test_v1(self):
        self.change_receipt(api_version=1, exports=[])
        self.rejects('v2')

    def test_missing_export(self):
        self.change_receipt(exports=['Decoder_GetApiVersion'])
        self.rejects('v2')

    def test_hash_mismatch(self):
        (self.root / self.m['files'][-1]['path']).write_bytes(b'wrong')
        self.rejects('hash mismatch')

    def test_architecture(self):
        f = self.m['files'][-1]
        f.update(self.put(f['path'], b'not a binary'))
        self.rejects('architecture')

    def test_stale_managed(self):
        self.change_receipt(managed={})
        self.rejects('stale')

    def test_stale_parser(self):
        self.change_receipt(parser_sha256='stale')
        self.rejects('stale')

    def test_wrong_host(self):
        self.change_receipt(execution_host_rid='linux-x64')
        self.rejects('matching RID')

    def test_failed_consumer(self):
        self.change_receipt(error_pass=False)
        self.rejects('matching RID')

    def test_nonfree_shipping(self):
        self.change_receipt(distribution='NONFREE/UNREDISTRIBUTABLE')
        self.rejects('distribution not approved')

    def test_local(self):
        self.m['files'] = self.m['files'][:4] + self.m['files'][-1:]
        self.m.update(lane='local', version='3.1.2-local.fixture')
        self.change_receipt(distribution='NONFREE/UNREDISTRIBUTABLE')
        self.write_spec()
        self.check()

    def test_local_version(self):
        self.m['lane'] = 'local'
        self.rejects('unique')

    def test_nuspec_substitution(self):
        self.spec.write_text(self.spec.read_text().replace('lib/net8.0/AAXClean.Codecs.dll', 'unexpected.dll'))
        with self.assertRaises((ValueError, FileNotFoundError)):
            self.check()

    def test_parser_range(self):
        self.spec.write_text(self.spec.read_text().replace('[3.1.1-local.test]', '3.1.1-local.test'))
        self.rejects('exact')

    def test_duplicate_framework_group(self):
        self.spec.write_text(self.spec.read_text().replace('targetFramework="net10.0"', 'targetFramework="net8.0"'))
        self.rejects('framework')

    def test_ungrouped_parser_dependency(self):
        spec = ET.parse(self.spec)
        deps = spec.find('./metadata/dependencies')
        for group in list(deps):
            for dep in list(group):
                deps.append(dep)
            deps.remove(group)
        spec.write(self.spec)
        self.rejects('framework')

    def test_archive_framework_substitution(self):
        archive = self.root / 'wrong-framework.nupkg'
        with zipfile.ZipFile(archive, 'w') as z:
            z.writestr('AAXClean.Codecs.nuspec', self.spec.read_text().replace('targetFramework="net10.0"', 'targetFramework="net9.0"'))
            for f in self.m['files']:
                z.write(self.root / f['path'], f['target'])
        with self.assertRaisesRegex(ValueError, 'framework'):
            self.check(archive)

    def test_archive_tampering(self):
        archive = self.root / 'test.nupkg'
        with zipfile.ZipFile(archive, 'w') as z:
            z.write(self.spec, 'AAXClean.Codecs.nuspec')
            for f in self.m['files']:
                z.write(self.root / f['path'], f['target'])
        self.check(archive)
        with zipfile.ZipFile(archive, 'a') as z:
            z.writestr('runtimes/unexpected/native/library', b'v1')
        with self.assertRaisesRegex(ValueError, 'unexpected archive payload'):
            self.check(archive)

    def test_missing_execution_log(self):
        self.change_receipt(execution_log={'path': 'missing.log', 'sha256': '0' * 64})
        with self.assertRaises(FileNotFoundError):
            self.check()

    def test_truncated_binary(self):
        f = self.m['files'][-1]
        f.update(self.put(f['path'], b'MZ'))
        with self.assertRaises(struct.error):
            self.check()

    def test_path_escape(self):
        self.m['files'][0]['path'] = '../outside.dll'
        self.rejects('unsafe')

    def test_shipped_manifest_is_blocked(self):
        manifest = json.loads(Path(__file__).with_name('artifacts.json').read_text())
        with self.assertRaisesRegex(ValueError, 'version mismatch'):
            gate.validate(self.root, manifest, self.spec, '3.1.2')


if __name__ == '__main__':
    unittest.main()
