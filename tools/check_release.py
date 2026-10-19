# -*- coding: utf-8 -*-
"""发布前自检：确认仓库里没有泄露签发私钥、也没有残留作者的服务端地址。

用法：
    python tools/check_release.py

检查项：
  1) bin/ 下的 exe 里不能出现私钥指数 D 的原始 256 字节（出现即说明私钥被明文编进去了）
  2) src/、tools/、docs/ 与 README 里不能出现真实服务端域名（本脚本自身除外）
  3) keys/、src/offline.cs、src/pem_local.cs 必须被 .gitignore 忽略

退出码 0 = 通过，1 = 发现问题。
"""
import glob
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PRIV = os.path.join(ROOT, 'keys', 'mykey_priv.pem')
SELF = os.path.relpath(os.path.abspath(__file__), ROOT)

# 只放"真实服务端/作者私有标识"，不要把 PEM 头之类的通用词放进来（否则脚本会自己命中自己）
BAD_WORDS = ['wanli365', 'kingcaisoft']
TEXT_EXT = ('.cs', '.py', '.md', '.cmd', '.bat', '.txt', '.json', '.yml', '.yaml')

problems = []


def load_d():
    if not os.path.exists(PRIV):
        return None
    try:
        from cryptography.hazmat.primitives import serialization
    except Exception:
        print('[!] 没有 cryptography 库，跳过私钥泄露扫描（建议 pip install cryptography）')
        return None
    key = serialization.load_pem_private_key(open(PRIV, 'rb').read(), password=None)
    d = key.private_numbers().d
    return d.to_bytes((d.bit_length() + 7) // 8, 'big')


def check_binary(dbytes):
    for exe in glob.glob(os.path.join(ROOT, 'bin', '*.exe')) + glob.glob(os.path.join(ROOT, 'bin', '*.dll')):
        raw = open(exe, 'rb').read()
        if dbytes in raw:
            problems.append('私钥指数 D 出现在二进制里：%s' % os.path.relpath(exe, ROOT))


def check_text():
    files = []
    for sub in ('src', 'tools', 'docs'):
        for dirpath, _dirnames, filenames in os.walk(os.path.join(ROOT, sub)):
            for fn in filenames:
                if fn.endswith(TEXT_EXT):
                    files.append(os.path.join(dirpath, fn))
    for fn in ('README.md', '.gitignore'):
        p = os.path.join(ROOT, fn)
        if os.path.exists(p):
            files.append(p)
    for p in files:
        rel = os.path.relpath(p, ROOT)
        if rel.replace('\\', '/') == SELF.replace('\\', '/'):
            continue
        try:
            s = open(p, encoding='utf-8', errors='ignore').read()
        except Exception:
            continue
        for w in BAD_WORDS:
            if w in s:
                problems.append('文本残留 %r：%s' % (w, rel))


def check_gitignore():
    if not os.path.isdir(os.path.join(ROOT, '.git')):
        print('[i] 还不是 git 仓库，跳过 .gitignore 校验')
        return
    for rel in ('keys/mykey_priv.pem', 'src/offline.cs', 'src/pem_local.cs', 'src/proxy_data.cs'):
        p = os.path.join(ROOT, rel.replace('/', os.sep))
        if not os.path.exists(p):
            continue
        r = subprocess.run(['git', 'check-ignore', '--no-index', '-q', rel],
                           cwd=ROOT, capture_output=True)
        if r.returncode != 0:
            problems.append('%s 没有被 .gitignore 忽略' % rel)


def main():
    d = load_d()
    if d is None:
        print('[i] 未找到私钥（keys/mykey_priv.pem），只做文本检查')
    else:
        print('[+] 已加载私钥 D（%d 字节），扫描二进制……' % len(d))
        check_binary(d)
    check_text()
    check_gitignore()

    if problems:
        print('\n[FAIL] 发现 %d 个问题：' % len(problems))
        for p in problems:
            print('  - ' + p)
        return 1
    print('\n[OK] 未发现私钥泄露/服务端地址残留。')
    return 0


if __name__ == '__main__':
    sys.exit(main())
