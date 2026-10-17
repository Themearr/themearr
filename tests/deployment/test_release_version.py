"""Execute the shipped semver workflow step against isolated real Git histories."""
import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]


class ReleaseVersionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.base = Path(self.tmp.name)
        self.repo = self.base / 'checkout'
        self.repo.mkdir()
        self.git('init', '--quiet', '--initial-branch=main')
        self.git('config', 'user.name', 'Release fixture')
        self.git('config', 'user.email', 'release-fixture@example.invalid')
        self.git('init', '--quiet', '--bare', str(self.base / 'origin.git'))
        self.git('remote', 'add', 'origin', str(self.base / 'origin.git'))

    def git(self, *arguments):
        result = subprocess.run(['git', '-c', 'core.hooksPath=/dev/null', '-c', 'commit.gpgsign=false', *arguments],
                                cwd=self.repo, capture_output=True, text=True, check=True, timeout=10)
        return result.stdout.strip()

    def commit(self, message):
        self.git('commit', '--quiet', '--allow-empty', '-m', message)
        return self.git('rev-parse', 'HEAD')

    def published_release(self):
        revision = self.commit('chore: published release')
        self.git('tag', 'v1.0.0')
        return revision

    def version(self, before):
        self.git('push', '--quiet', 'origin', 'HEAD:refs/heads/main', '--tags')
        revision = self.git('rev-parse', 'HEAD')
        lines = (ROOT / '.github/workflows/release.yml').read_text().splitlines()
        step = next(i for i, line in enumerate(lines) if line.strip() == 'id: semver')
        start = next(i for i in range(step, len(lines)) if lines[i].strip() == 'run: |') + 1
        end = start
        while end < len(lines) and (not lines[end].strip() or lines[end].startswith('          ')):
            end += 1
        script = textwrap.dedent('\n'.join(lines[start:end]))
        script = script.replace('${{ github.event.before }}', before).replace('${{ github.sha }}', revision)
        output = self.base / 'github-output'
        result = subprocess.run(['bash', '-c', script], cwd=self.repo,
                                env={**os.environ, 'GITHUB_OUTPUT': str(output)},
                                capture_output=True, text=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        return dict(line.split('=', 1) for line in output.read_text().splitlines())

    def test_unpublished_feature_survives_a_superseded_pending_push(self):
        self.published_release()
        pending_feature = self.commit('feat: new library integration')
        self.commit('fix: correct follow-up')
        result = self.version(before=pending_feature)
        self.assertEqual('minor', result['bump'])
        self.assertEqual('v1.1.0', result['next_tag'])

    def test_ordinary_fix_after_published_tag_remains_patch(self):
        published = self.published_release()
        self.commit('fix: correct update status')
        result = self.version(before=published)
        self.assertEqual('patch', result['bump'])
        self.assertEqual('v1.0.1', result['next_tag'])

    def test_initial_release_considers_full_untagged_history(self):
        self.commit('feat: first library integration')
        self.commit('fix: final release correction')
        result = self.version(before='0' * 40)
        self.assertEqual('v0.0.0', result['latest_tag'])
        self.assertEqual('minor', result['bump'])
        self.assertEqual('v0.1.0', result['next_tag'])

    def test_manual_dispatch_with_empty_before_uses_unpublished_history(self):
        self.published_release()
        self.commit('feat: new library integration')
        self.commit('fix: final release correction')
        result = self.version(before='')
        self.assertEqual('minor', result['bump'])
        self.assertEqual('v1.1.0', result['next_tag'])


if __name__ == '__main__':
    unittest.main()
