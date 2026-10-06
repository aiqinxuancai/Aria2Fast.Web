"""Portable regression tests for the macOS bundler's Mach-O command sequence.

Real Mach-O editing and execution are additionally tested on both macOS CI runners.
"""
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/bundle-macos.py'


class BundleTests(unittest.TestCase):
    def exercise(self, fail_edit=False):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            prefix = root / 'brew'
            source = prefix / 'bin/aria2c'
            crypto = prefix / 'lib/libcrypto.3.dylib'
            ssh = prefix / 'lib/libssh2.1.dylib'
            for path in (source, crypto, ssh):
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b'signed Mach-O fixture')
            destination = root / 'bundle'
            dependencies = {source: [str(ssh)], ssh: [str(ssh), str(crypto)], crypto: [str(crypto)]}
            calls = []

            def output(args, **kwargs):
                if args[:2] == ('brew', '--prefix'):
                    return str(prefix)
                if args[:2] == ('brew', 'deps'):
                    return ''
                if args[:2] == ('brew', 'info'):
                    return '{}'
                if args[:2] == ('otool', '-l'):
                    return 'cmd LC_RPATH\ncmdsize 48\npath /opt/homebrew/lib (offset 12)'
                if args[:2] == ('otool', '-L'):
                    path = Path(args[2])
                    deps = dependencies.get(path, ['@loader_path/libcrypto.3.dylib'])
                    return str(path) + ':\n' + '\n'.join('\t' + dep + ' (compatibility version 1.0.0)' for dep in deps) + '\n\t/usr/lib/libSystem.B.dylib (compatibility version 1.0.0)'
                raise AssertionError(args)

            def run(args, **kwargs):
                calls.append(args)
                self.assertNotIn('--remove-signature', args)
                self.assertTrue(kwargs.get('check'), args)
                if fail_edit and args[0] == 'install_name_tool':
                    raise subprocess.CalledProcessError(1, args)
                return subprocess.CompletedProcess(args, 0)

            with patch.object(sys, 'argv', [str(SCRIPT), str(destination)]), patch('subprocess.check_output', side_effect=output), patch('subprocess.run', side_effect=run):
                if fail_edit:
                    with self.assertRaises(subprocess.CalledProcessError):
                        runpy.run_path(str(SCRIPT), run_name='__main__')
                    self.assertFalse(any('--sign' in call for call in calls))
                    self.assertFalse((destination / 'bundle-manifest.json').exists())
                    return
                runpy.run_path(str(SCRIPT), run_name='__main__')

            edits = [call for call in calls if call[0] == 'install_name_tool']
            self.assertEqual(len(edits), 3)
            self.assertEqual(len({call[-1] for call in edits}), 3)
            ssh_edit = next(call for call in edits if call[-1].endswith('libssh2.1.dylib'))
            self.assertIn('-change', ssh_edit)
            self.assertIn('-id', ssh_edit)
            self.assertIn('-delete_rpath', ssh_edit)
            last_edit = max(i for i, call in enumerate(calls) if call[0] == 'install_name_tool')
            for target in destination.glob('*'):
                if target.is_file() and target.name != 'bundle-manifest.json':
                    sign = ['codesign', '--force', '--sign', '-', str(target)]
                    verify = ['codesign', '--verify', '--strict', str(target)]
                    self.assertGreater(calls.index(sign), last_edit)
                    self.assertGreater(calls.index(verify), calls.index(sign))
            self.assertEqual(calls[-1], [str(destination / 'aria2c'), '--version'])

    def test_edit_before_resign_and_verify(self):
        self.exercise()

    def test_edit_failure_aborts_bundle(self):
        self.exercise(fail_edit=True)


if __name__ == '__main__':
    unittest.main()
