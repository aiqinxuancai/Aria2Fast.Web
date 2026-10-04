"""Apply the repository encoding/line ending conventions, excluding generated output."""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
excluded = {'bin', 'obj', '.git', 'artifacts', 'node_modules', 'scaffold-recovery', '__pycache__'}
extensions = {'.cs', '.csproj', '.sln', '.json', '.js', '.cjs', '.css', '.html', '.md', '.ps1', '.cmd', '.py', '.yml', '.yaml', '.sh', '.command'}
count = 0
for path in root.rglob('*'):
    if not path.is_file() or any(part in excluded for part in path.relative_to(root).parts):
        continue
    if path.suffix not in extensions and path.name not in {'Dockerfile', '.editorconfig', '.gitattributes', '.gitignore', '.dockerignore', '.env.example'}:
        continue
    text = path.read_text(encoding='utf-8-sig').replace('\r\n', '\n').replace('\r', '\n')
    ending = '\n' if path.suffix in {'.yml', '.yaml', '.sh', '.command'} or path.name == 'Dockerfile' else '\r\n'
    encoding = 'utf-8-sig' if path.suffix == '.cs' else 'utf-8'
    path.write_bytes(text.replace('\n', ending).encode(encoding))
    count += 1
print(f'Normalized {count} source files.')
