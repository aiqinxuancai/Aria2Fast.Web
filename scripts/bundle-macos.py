"""Bundle Homebrew aria2 and its non-system dylibs into a relocatable directory."""
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys


def command(*args):
    return subprocess.check_output(args, text=True).strip()


destination = Path(sys.argv[1]).resolve()
destination.mkdir(parents=True, exist_ok=True)
source = Path(command('brew', '--prefix', 'aria2')) / 'bin/aria2c'
copied = {}


def bundle(original):
    original = original.resolve()
    if original in copied:
        return copied[original]
    target = destination / original.name
    if target.exists() and target not in copied.values():
        raise RuntimeError(f'Duplicate library filename: {target.name}')
    shutil.copy2(original, target)
    target.chmod(0o755)
    copied[original] = target
    # Do not strip the Homebrew signature first: codesign --remove-signature
    # can leave __LINKEDIT padding that Apple's install_name_tool rejects.
    # Apply all load-command edits together, then replace the invalidated signature.
    edits = []
    lines = command('otool', '-L', str(original)).splitlines()[1:]
    for line in lines:
        dep = line.strip().split(' (')[0]
        if dep.startswith(('/System/', '/usr/lib/')):
            continue
        if dep.startswith('@loader_path/'):
            dependency = original.parent / dep.removeprefix('@loader_path/')
        elif dep.startswith('@executable_path/'):
            dependency = source.parent / dep.removeprefix('@executable_path/')
        elif dep.startswith('@rpath/'):
            rpaths = re.findall(r'cmd LC_RPATH\s+cmdsize \d+\s+path (\S+)', command('otool', '-l', str(original)))
            roots = [Path(p.replace('@loader_path', str(original.parent)).replace('@executable_path', str(source.parent))) for p in rpaths]
            suffix = dep.removeprefix('@rpath/')
            dependency = next((root / suffix for root in roots if (root / suffix).exists()), None)
            if dependency is None:
                raise RuntimeError(f'Unresolved dependency {dep} in {original}')
        else:
            dependency = Path(dep)
        if dependency.resolve() == original:
            continue
        packaged = bundle(dependency)
        edits.extend(['-change', dep, '@loader_path/' + packaged.name])
    if original.suffix == '.dylib':
        edits.extend(['-id', '@loader_path/' + target.name])
    rpaths = re.findall(r'cmd LC_RPATH\s+cmdsize \d+\s+path (\S+)', command('otool', '-l', str(original)))
    for path in dict.fromkeys(rpaths):
        if path.startswith(('/opt/homebrew', '/usr/local', '/Users/')):
            edits.extend(['-delete_rpath', path])
    if edits:
        subprocess.run(['install_name_tool', *edits, str(target)], check=True)
    return target


binary = bundle(source)
for target in copied.values():
    subprocess.run(['codesign', '--force', '--sign', '-', str(target)], check=True)
    subprocess.run(['codesign', '--verify', '--strict', str(target)], check=True)
    dependencies = command('otool', '-L', str(target))
    if any(prefix in dependencies for prefix in ('/opt/homebrew/', '/usr/local/Cellar/', '/usr/local/opt/')):
        raise RuntimeError(f'Non-relocatable library: {target}')
subprocess.run([str(binary), '--version'], check=True, cwd='/tmp')

license_dir = destination / 'licenses'
license_dir.mkdir(exist_ok=True)
formulae = ['aria2'] + command('brew', 'deps', '--installed', 'aria2').splitlines()
for formula in formulae:
    prefix = Path(command('brew', '--prefix', formula))
    target = license_dir / formula.replace('/', '_')
    target.mkdir(exist_ok=True)
    for pattern in ('LICENSE*', 'COPYING*', 'NOTICE*'):
        for path in prefix.glob(pattern):
            if path.is_file():
                shutil.copy2(path, target / path.name)
    (target / 'formula.json').write_text(command('brew', 'info', '--json=v2', formula), encoding='utf-8')
(destination / 'bundle-manifest.json').write_text(json.dumps({str(k): v.name for k, v in copied.items()}, indent=2), encoding='utf-8')
