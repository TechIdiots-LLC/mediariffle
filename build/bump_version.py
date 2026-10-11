# Bumps <Version> in the server project and rolls the "## master" section of CHANGELOG.md into a new version section.
# Used by the GitLab bump-version job (.gitlab/scripts/bump.ps1) and the GitHub "Create bump version PR" workflow.
# Based on VistumblerMAUI's bump_version.py.
#
# Usage: python build/bump_version.py <patch|minor|major|prepatch|preminor|premajor|prerelease> [preid]
# Writes the new version to bump_version.txt, and to $GITHUB_OUTPUT (version=...) when run on GitHub.
import json, os, re, subprocess, sys, urllib.request

version_type   = sys.argv[1]
preid          = sys.argv[2] if len(sys.argv) > 2 and sys.argv[2].strip() else 'rc'
csproj_path    = 'src/MediaRiffle.Server/MediaRiffle.Server.csproj'
changelog_path = 'CHANGELOG.md'


# Files are UTF-8 (possibly with a BOM), with CRLF line endings on a Windows runner. Read them as UTF-8 (not the
# Windows ANSI default, which fails on the changelog's emoji) and write them back the same way.
def read_text(path):
    raw = open(path, 'rb').read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    newline = '\r\n' if b'\r\n' in raw else '\n'
    return raw.decode('utf-8-sig').replace('\r\n', '\n'), bom, newline


def write_text(path, text, bom, newline):
    with open(path, 'w', encoding='utf-8-sig' if bom else 'utf-8', newline=newline) as f:
        f.write(text)


csproj, csproj_bom, csproj_nl = read_text(csproj_path)
m = re.search(r'<Version>([^<]+)</Version>', csproj)
if not m:
    raise RuntimeError(f"Could not find <Version> in {csproj_path}")
current = m.group(1).strip()
print(f"Current version: {current}")

pre_match = re.match(r'^(\d+)\.(\d+)\.(\d+)-(.+?)\.(\d+)$', current)
rel_match = re.match(r'^(\d+)\.(\d+)\.(\d+)$', current)
if pre_match:
    major, minor, patch = int(pre_match.group(1)), int(pre_match.group(2)), int(pre_match.group(3))
    pre_tag, pre_num, is_pre = pre_match.group(4), int(pre_match.group(5)), True
elif rel_match:
    major, minor, patch = int(rel_match.group(1)), int(rel_match.group(2)), int(rel_match.group(3))
    pre_tag, pre_num, is_pre = None, 0, False
else:
    raise RuntimeError(f"Unrecognised version format: {current}")

if version_type == 'patch':
    new_version = f"{major}.{minor}.{patch}" if is_pre else f"{major}.{minor}.{patch + 1}"
elif version_type == 'minor':
    new_version = f"{major}.{minor}.0" if is_pre else f"{major}.{minor + 1}.0"
elif version_type == 'major':
    new_version = f"{major}.0.0" if is_pre else f"{major + 1}.0.0"
elif version_type == 'prepatch':
    new_version = f"{major}.{minor}.{patch + 1}-{preid}.1"
elif version_type == 'preminor':
    new_version = f"{major}.{minor + 1}.0-{preid}.1"
elif version_type == 'premajor':
    new_version = f"{major + 1}.0.0-{preid}.1"
elif version_type == 'prerelease':
    new_version = f"{major}.{minor}.{patch}-{pre_tag}.{pre_num + 1}" if is_pre else f"{major}.{minor}.{patch + 1}-{preid}.1"
else:
    raise RuntimeError(f"Unknown version type: {version_type}")
print(f"New version: {new_version}")

csproj = re.sub(r'<Version>[^<]+</Version>', f'<Version>{new_version}</Version>', csproj, count=1)
write_text(csproj_path, csproj, csproj_bom, csproj_nl)
print(f"Updated {csproj_path}")

# ── CHANGELOG.md ─────────────────────────────────────────────────────────────
if not os.path.exists(changelog_path):
    write_text(changelog_path, '# Changelog\n\n', False, csproj_nl)
changelog, changelog_bom, changelog_nl = read_text(changelog_path)

# GitLab: merge requests since the last tag that the changelog doesn't mention yet
missing_entries = []
api, project, token = os.environ.get('CI_API_V4_URL'), os.environ.get('CI_PROJECT_ID'), os.environ.get('CI_PUSH_TOKEN')
if api and project and token:
    try:
        latest_tag = subprocess.check_output(['git', 'describe', '--tags', '--abbrev=0'], text=True).strip()
        commit_range = f"{latest_tag}..HEAD"
    except subprocess.CalledProcessError:
        commit_range = 'HEAD'
    try:
        commits = subprocess.check_output(['git', 'log', commit_range, '--format=%B'], text=True, encoding='utf-8')
    except subprocess.CalledProcessError:
        commits = ''
    for iid in [n for n in dict.fromkeys(re.findall(r'!(\d+)\b', commits)) if f'!{n}' not in changelog]:
        try:
            req = urllib.request.Request(f"{api}/projects/{project}/merge_requests/{iid}", headers={'PRIVATE-TOKEN': token})
            mr = json.load(urllib.request.urlopen(req))
            if re.search(r'bot|dependabot|renovate', mr['author']['username'], re.I):
                continue
            missing_entries.append(f"- {mr['title']} ([!{mr['iid']}]({mr['web_url']})) (@{mr['author']['username']})")
        except Exception as e:
            print(f"Could not fetch merge request !{iid}: {e}")

changelog = changelog.replace('## master', f'## {new_version}', 1)
changelog = changelog.replace('- _...Add new stuff here..._\n', '')
master_section = '\n'.join([
    '## master',
    '### ✨ Features and improvements',
    '- _...Add new stuff here..._',
    '',
    '### 🐞 Bug fixes',
    '- _...Add new stuff here..._',
    '',
    '',
])
title_match = re.match(r'^(# .+?\n\n)', changelog)
title, rest = (title_match.group(1), changelog[len(title_match.group(1)):]) if title_match else ('# Changelog\n\n', changelog)
if missing_entries:
    bf = re.search(r'^(## [^\n]+\n### ✨ Features and improvements\n(?:.*\n)*?### 🐞 Bug fixes\n)', rest, re.MULTILINE)
    entries = '\n'.join(missing_entries) + '\n'
    rest = rest[:bf.end()] + entries + rest[bf.end():] if bf else rest + '\n' + entries
write_text(changelog_path, title + master_section + rest, changelog_bom, changelog_nl)
print("Updated CHANGELOG.md")

open('bump_version.txt', 'w', encoding='utf-8').write(new_version)
if os.environ.get('GITHUB_OUTPUT'):
    with open(os.environ['GITHUB_OUTPUT'], 'a') as f:
        f.write(f"version={new_version}\n")
