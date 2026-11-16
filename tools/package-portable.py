"""Empacota uma publicação existente, sem instalar nem iniciar o aplicativo."""
import argparse
import hashlib
import pathlib
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('release_directory', type=pathlib.Path)
parser.add_argument('--version', required=True)
args = parser.parse_args()
release = args.release_directory.resolve()
publish = release / 'publish'
if not (publish / 'ShiftControl.exe').is_file():
    raise SystemExit('Publicação não encontrada.')
target = release / f'ShiftControl-{args.version}-portable.zip'
temporary = target.with_suffix('.zip.part')
with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=4) as archive:
    for item in sorted(publish.rglob('*')):
        if item.is_file():
            archive.write(item, pathlib.Path('ShiftControl') / item.relative_to(publish))
temporary.replace(target)
with zipfile.ZipFile(target) as archive:
    bad_file = archive.testzip()
    if bad_file:
        raise SystemExit(f'Arquivo corrompido no pacote: {bad_file}')
    if 'ShiftControl/ShiftControl.exe' not in archive.namelist():
        raise SystemExit('Executável ausente no pacote.')
with target.open('rb') as stream:
    digest = hashlib.file_digest(stream, 'sha256').hexdigest()
target.with_suffix('.zip.sha256').write_text(f'{digest}  {target.name}\n', encoding='ascii')
print(f'{target.name}: {target.stat().st_size} bytes; CRC e SHA-256 verificados.')
