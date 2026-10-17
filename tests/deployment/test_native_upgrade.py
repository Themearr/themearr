"""Run real deployment scripts in a disposable filesystem with fake host commands.

Only hardcoded host paths are relocated in test copies; no installer is run against
an actual system service. Ownership calls are recorded because these tests run unprivileged.
"""
import hashlib
import io
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class NativeUpgradeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.base = Path(self.tmp.name)
        self.install = self.base / 'opt/themearr'
        self.data = self.install / 'data'
        self.data.mkdir(parents=True)
        self.bin = self.base / 'bin'
        self.bin.mkdir()
        self.helper = self.base / 'usr/local/bin/themearr-update'
        self.helper.parent.mkdir(parents=True)
        (self.base / 'etc/sudoers.d').mkdir(parents=True)
        (self.base / 'etc/systemd/system').mkdir(parents=True)
        (self.data / 'themearr.db').write_text('before-download')
        (self.data / 'auth.env').write_text('THEMEARR_AUTH_TOKEN=existing-secret\n')
        (self.install / 'custom.conf').write_text('keep my config')
        (self.install / 'Themearr.API.dll').write_text('old-binary')
        self.asset = self.base / 'release.tar.gz'
        self.release({'Themearr.API.dll': b'new-binary', 'deploy.sh': (ROOT / 'deploy.sh').read_bytes()})
        self.fake('curl', '''
case "$*" in
  *api.github.com*) cat "$FIXTURE/release.json" ;;
  *sha256*) cat "$FIXTURE/checksum" ;;
  *raw.githubusercontent.com*) exit 22 ;;
  *)
    printf live-write > "$FIXTURE/opt/themearr/data/themearr.db"
    while [ "$#" -gt 0 ]; do
      if [ "$1" = -o ]; then cp "$FIXTURE/release.tar.gz" "$2"; exit; fi
      shift
    done
    exit 1 ;;
esac
''')
        self.fake('uname', 'echo x86_64')
        self.fake('dotnet', 'echo "Microsoft.AspNetCore.App 10.0.0 [/fake]"')
        self.fake('id', 'echo 999')
        self.fake('chown', 'printf "%s\\n" "$*" >> "$FIXTURE/ownership"')
        for cmd in ['systemctl', 'systemd-run', 'visudo', 'useradd']:
            self.fake(cmd, 'exit 0')
        self.fake('mktemp', "python3 -c 'import tempfile; print(tempfile.mkdtemp(prefix=\"themearr-test-\", dir=__import__(\"os\").environ[\"FIXTURE\"]) if \"-d\" in __import__(\"sys\").argv else tempfile.mkstemp(prefix=\"themearr-test-\", dir=__import__(\"os\").environ[\"FIXTURE\"])[1])' \"$@\"")
        self.fake('sha256sum', 'shasum -a 256 "$@"')
        self.env = {**os.environ, 'PATH': f'{self.bin}:{os.environ["PATH"]}', 'FIXTURE': str(self.base)}

    def fake(self, name, body):
        path = self.bin / name
        path.write_text('#!/bin/bash\nset -eu\n' + body + '\n')
        path.chmod(0o755)

    def release(self, files):
        with tarfile.open(self.asset, 'w:gz') as archive:
            for name, content in files.items():
                entry = tarfile.TarInfo('themearr-v1.2.3/' + name)
                entry.size = len(content)
                entry.mode = 0o755 if name.endswith('.sh') else 0o644
                archive.addfile(entry, io.BytesIO(content))
        (self.base / 'checksum').write_text(hashlib.sha256(self.asset.read_bytes()).hexdigest())

    def relocate(self, text):
        for original in ['/opt/themearr', '/usr/local/bin/themearr-update', '/etc/sudoers.d/themearr', '/etc/systemd/system/themearr.service']:
            text = text.replace(original, str(self.base) + original)
        return text

    def run_script(self, script, checksum=True):
        checksum_entry = ',\n  {"browser_download_url": "https://fixture/linux-x64.tar.gz.sha256"}' if checksum else ''
        (self.base / 'release.json').write_text('''{
  "tag_name": "v1.2.3",
  "assets": [
  {"browser_download_url": "https://fixture/linux-x64.tar.gz"}''' + checksum_entry + '\n]}')
        path = self.base / script
        path.write_text(self.relocate((ROOT / script).read_text()))
        return subprocess.run(['bash', str(path)], env=self.env, capture_output=True, text=True, timeout=10)

    def test_install_and_upgrade_accept_older_release_without_checksum(self):
        for script in ['install.sh', 'deploy.sh']:
            with self.subTest(script=script):
                result = self.run_script(script, checksum=False)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual('new-binary', (self.install / 'Themearr.API.dll').read_text())

    def test_upgrade_preserves_database_written_while_release_downloads(self):
        result = self.run_script('deploy.sh')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual('live-write', (self.data / 'themearr.db').read_text())
        self.assertEqual('THEMEARR_AUTH_TOKEN=existing-secret\n', (self.data / 'auth.env').read_text())
        self.assertEqual('keep my config', (self.install / 'custom.conf').read_text())

    def test_fresh_install_keeps_code_root_owned_and_only_data_service_owned(self):
        result = self.run_script('install.sh')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        ownership = (self.base / 'ownership').read_text().splitlines()
        self.assertIn(f'root:themearr {self.install}', ownership)
        self.assertFalse(any('root:root' in entry and str(self.data) in entry for entry in ownership))
        self.assertNotIn(f'-R themearr:themearr {self.install}', ownership)
        self.assertIn(f'themearr:themearr {self.data}', ownership)

    def test_old_install_upgrade_repairs_helper_and_application_ownership(self):
        self.helper.write_text('#!/bin/bash\nexec bash "' + str(self.install / 'deploy.sh') + '"\n')
        self.helper.chmod(0o755)
        result = self.run_script('deploy.sh')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        ownership = (self.base / 'ownership').read_text().splitlines()
        self.assertIn(f'root:themearr {self.install}', ownership)
        self.assertFalse(any('root:root' in entry and str(self.data) in entry for entry in ownership))
        self.assertIn(f'root:root {self.helper}', ownership)
        self.assertIn(f'themearr:themearr {self.data}', ownership)

    def test_first_upgraded_startup_repairs_through_legacy_helper_without_network(self):
        (self.data / '.repair-permissions').touch()
        (self.install / 'deploy.sh').write_text(self.relocate((ROOT / 'deploy.sh').read_text()))
        self.helper.write_text('#!/bin/bash\nexec bash "' + str(self.install / 'deploy.sh') + '"\n')
        self.helper.chmod(0o755)
        self.fake('curl', 'echo unexpected-network >&2; exit 99')
        result = subprocess.run(['bash', str(self.helper), '--repair-permissions'], env=self.env, capture_output=True, text=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertFalse((self.data / '.repair-permissions').exists())
        self.assertEqual('before-download', (self.data / 'themearr.db').read_text())
        ownership = (self.base / 'ownership').read_text().splitlines()
        self.assertIn(f'root:themearr {self.install}', ownership)
        self.assertFalse(any('root:root' in entry and str(self.data) in entry for entry in ownership))
        self.assertIn(f'root:root {self.helper}', ownership)

    def test_upgrade_replaces_binary_without_truncating_running_process_inode(self):
        with (self.install / 'Themearr.API.dll').open() as mapped:
            result = self.run_script('deploy.sh')
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual('old-binary', mapped.read())
        self.assertEqual('new-binary', (self.install / 'Themearr.API.dll').read_text())

    def test_data_symlink_is_rejected_without_touching_outside_tree(self):
        outside = self.base / 'outside'
        outside.mkdir(mode=0o755)
        sentinel = outside / 'auth.env'
        sentinel.write_text('outside-secret')
        sentinel.chmod(0o644)
        shutil.rmtree(self.data)
        self.data.symlink_to(outside, target_is_directory=True)
        for script in ['install.sh', 'deploy.sh']:
            with self.subTest(script=script):
                result = self.run_script(script)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(0o755, outside.stat().st_mode & 0o777)
                self.assertEqual(0o644, sentinel.stat().st_mode & 0o777)
                self.assertEqual('outside-secret', sentinel.read_text())
                self.assertFalse((self.base / 'ownership').exists())

    def test_upgrade_never_chmods_a_service_controlled_auth_symlink(self):
        outside = self.base / 'outside-auth'
        outside.write_text('existing-token')
        outside.chmod(0o644)
        (self.data / 'auth.env').unlink()
        (self.data / 'auth.env').symlink_to(outside)
        result = self.run_script('deploy.sh')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(0o644, outside.stat().st_mode & 0o777)
        self.assertEqual('existing-token', outside.read_text())

    def test_version_write_replaces_legacy_symlink_without_touching_target(self):
        outside = self.base / 'outside-version'
        outside.write_text('outside config')
        (self.install / 'VERSION').symlink_to(outside)
        for script in ['install.sh', 'deploy.sh']:
            with self.subTest(script=script):
                result = self.run_script(script)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual('outside config', outside.read_text())
                self.assertEqual('v1.2.3', (self.install / 'VERSION').read_text().strip())

    def test_existing_native_appsettings_are_preserved_when_release_ships_defaults(self):
        config = self.install / 'appsettings.json'
        config.write_text('{"Themearr":{"CustomSetting":"keep-me"}}')
        self.release({'Themearr.API.dll': b'new-binary', 'appsettings.json': b'{"Themearr":{}}'})
        for script in ['install.sh', 'deploy.sh']:
            with self.subTest(script=script):
                result = self.run_script(script)
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual('{"Themearr":{"CustomSetting":"keep-me"}}', config.read_text())

    def test_private_legacy_config_remains_readable_to_service_group_after_root_repair(self):
        self.install.chmod(0o700)
        config = self.install / 'appsettings.json'
        config.write_text('{"Themearr":{"CustomSetting":"keep-me"}}')
        config.chmod(0o600)
        nested = self.install / 'private-overrides'
        nested.mkdir(mode=0o700)
        secret = nested / 'override.json'
        secret.write_text('private-config-bytes')
        secret.chmod(0o600)
        result = self.run_script('deploy.sh')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(0o750, self.install.stat().st_mode & 0o777)
        self.assertEqual(0o640, config.stat().st_mode & 0o777)
        self.assertEqual(0o750, nested.stat().st_mode & 0o777)
        self.assertEqual(0o640, secret.stat().st_mode & 0o777)
        self.assertEqual('{"Themearr":{"CustomSetting":"keep-me"}}', config.read_text())
        self.assertEqual('private-config-bytes', secret.read_text())
        ownership = (self.base / 'ownership').read_text().splitlines()
        root_service_changes = [entry for entry in ownership if 'root:themearr' in entry]
        self.assertTrue(any(str(self.install) in entry for entry in root_service_changes))
        self.assertTrue(any(str(config) in entry for entry in root_service_changes))
        self.assertTrue(any(str(nested) in entry for entry in root_service_changes))
        self.assertTrue(any(str(secret) in entry for entry in root_service_changes))
        self.assertFalse(any('root:' in entry and str(self.data) in entry for entry in ownership))
        self.assertIn(f'root:root {self.helper}', ownership)
        self.assertEqual(0o755, self.helper.stat().st_mode & 0o777)

    def test_failed_download_keeps_legacy_private_parent_service_readable(self):
        self.fake('curl', '\ncase "$*" in\n  *api.github.com*) cat "$FIXTURE/release.json" ;;\n  *) exit 22 ;;\nesac\n')
        for script in ['install.sh', 'deploy.sh']:
            with self.subTest(script=script):
                self.install.chmod(0o750)
                ownership_file = self.base / 'ownership'
                ownership_file.unlink(missing_ok=True)
                result = self.run_script(script)
                self.assertNotEqual(0, result.returncode)
                self.assertEqual(0o750, self.install.stat().st_mode & 0o777)
                ownership = ownership_file.read_text().splitlines()
                self.assertIn(f'root:themearr {self.install}', ownership)
                self.assertNotIn(f'root:root {self.install}', ownership)
                self.assertEqual('before-download', (self.data / 'themearr.db').read_text())

    def test_root_run_legacy_upgrade_without_service_account_keeps_root_group(self):
        self.fake('id', 'exit 1')
        self.fake('chown', '\ncase "$*" in\n  *themearr:themearr*|*root:themearr*) exit 1 ;;\nesac\nprintf "%s\\n" "$*" >> "$FIXTURE/ownership"\n')
        result = self.run_script('deploy.sh')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        ownership = (self.base / 'ownership').read_text().splitlines()
        self.assertIn(f'root:root {self.install}', ownership)
        self.assertFalse(any('root:themearr' in entry or 'themearr:themearr' in entry for entry in ownership))
        self.assertEqual('new-binary', (self.install / 'Themearr.API.dll').read_text())

    def test_install_selects_service_group_only_after_creating_service_account(self):
        self.fake('id', 'test -f "$FIXTURE/account-created" || exit 1; echo 999')
        self.fake('useradd', 'touch "$FIXTURE/account-created"')
        self.fake('chown', '\ncase "$*" in\n  *themearr:themearr*|*root:themearr*) test -f "$FIXTURE/account-created" || exit 1 ;;\nesac\nprintf "%s\\n" "$*" >> "$FIXTURE/ownership"\n')
        result = self.run_script('install.sh')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        ownership = (self.base / 'ownership').read_text().splitlines()
        self.assertIn(f'root:root {self.install}', ownership)
        self.assertIn(f'root:themearr {self.install}', ownership)
        self.assertLess(ownership.index(f'root:root {self.install}'), ownership.index(f'root:themearr {self.install}'))
        self.assertIn(f'themearr:themearr {self.data}', ownership)

    def test_failed_archive_extraction_leaves_existing_binary_untouched(self):
        self.release({'Themearr.API.dll': b'partial-binary', 'data/themearr.db': b'archive-data'})
        self.fake('tar', '''
if [[ "$*" == *-xzf* ]]; then
  while [ "$#" -gt 0 ]; do
    if [ "$1" = -C ]; then printf partial-binary > "$2/Themearr.API.dll"; exit 2; fi
    shift
  done
else
  exec /usr/bin/tar "$@"
fi
''')
        result = self.run_script('deploy.sh')
        self.assertNotEqual(0, result.returncode)
        self.assertEqual('old-binary', (self.install / 'Themearr.API.dll').read_text())

    def test_archive_cannot_replace_live_data(self):
        self.release({'Themearr.API.dll': b'new-binary', 'data/themearr.db': b'archive-data'})
        result = self.run_script('deploy.sh')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual('live-write', (self.data / 'themearr.db').read_text())

    def test_helper_fallback_reports_failed_download_and_never_executes_partial_script(self):
        result = self.run_script('install.sh')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        (self.install / 'deploy.sh').unlink()
        self.fake('curl', 'printf "touch %s/executed\\n" "$FIXTURE"; exit 22')
        result = subprocess.run(['bash', str(self.helper)], env=self.env, capture_output=True, text=True, timeout=10)
        self.assertNotEqual(0, result.returncode)
        self.assertFalse((self.base / 'executed').exists())


if __name__ == '__main__':
    unittest.main()
