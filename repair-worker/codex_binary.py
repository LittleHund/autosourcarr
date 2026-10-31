"""Find and validate the fixed image-bundled native Codex account executable."""
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT=Path('/opt/node/lib/node_modules/@openai')
TARGET=Path('/opt/node/bin/codex-native')


def resolve_binary(root=ROOT):
    root=Path(root).resolve(strict=True)
    # Match npm's optional native package before the legacy bundled vendor tree.
    packages=('codex/node_modules/@openai/codex-linux-x64','codex-linux-x64','codex')
    for package in packages:
        for triple in ('x86_64-unknown-linux-musl','x86_64-unknown-linux-gnu'):
            # 0.157.0 uses bin/codex; older archives used codex/codex.
            for relative in ('bin/codex','codex/codex'):
                path=root/package/'vendor'/triple/relative
                if not path.exists():
                    continue
                if path.is_symlink() or not path.is_file():
                    raise RuntimeError('Bundled native Codex executable is not an ordinary file')
                resolved=path.resolve(strict=True)
                if not resolved.is_relative_to(root) or not os.access(resolved,os.X_OK):
                    raise RuntimeError('Bundled native Codex executable is outside its image tools or not executable')
                with resolved.open('rb') as stream:header=stream.read(20)
                if len(header)<20 or header[:6]!=b'\x7fELF\x02\x01' or header[18:20]!=b'\x3e\x00':
                    raise RuntimeError('Bundled native Codex executable is not a Linux x86-64 binary')
                return resolved
    raise RuntimeError('Bundled native Codex executable is missing from the image')


def link_and_check():
    binary=resolve_binary()
    if TARGET.exists() or TARGET.is_symlink():
        if not TARGET.is_symlink() or TARGET.resolve(strict=True)!=binary:
            raise RuntimeError('Refusing to replace an unexpected account executable')
    else:
        TARGET.symlink_to(binary)
    # Exercise the same inherited limits without starting sign-in or reading auth.
    result=subprocess.run([sys.executable,'-I',str(Path(__file__).with_name('resource_budget.py')),
                           '--',str(TARGET),'--version'],stdin=subprocess.DEVNULL,capture_output=True,text=True,timeout=30)
    output=result.stdout.strip()
    if result.returncode or len(output)>128 or not re.fullmatch(r'codex(?:-cli)? \d+\.\d+\.\d+[A-Za-z0-9.+-]*',output):
        raise RuntimeError('Bundled native Codex failed its bounded image-build version check')
    Path('/opt/autosourcarr-codex-version').write_text(output+'\n',encoding='utf-8')
    print(output)


if __name__=='__main__':
    if sys.argv[1:]!=['--link']:raise SystemExit('Expected --link during image build')
    link_and_check()
