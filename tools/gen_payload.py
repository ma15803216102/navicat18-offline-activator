# -*- coding: utf-8 -*-
"""把 src/pdata.cs 里 .pmod 跳板内嵌的验签模数换成「你自己的公钥模数」。

为什么必须做：
    shell 是注入 libcf.dll 的机器码，它在偏移 0x5F 处内嵌了 256 字节的 RSA 模数
    （小端）。工具安装补丁时，客户端验签会用这个模数——所以它必须等于
    keys/mykey_pub.pem 里的 N，否则你自己签的激活码会被判为无效。

用法：
    python tools/gen_payload.py            # 用当前公钥重写 src/pdata.cs 的 _shell
    python tools/gen_payload.py --check    # 只检查是否匹配（不写文件）

需要先生成密钥：python tools/gen_keys.py
"""
import argparse
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PDATA = os.path.join(ROOT, 'src', 'pdata.cs')
PUB = os.path.join(ROOT, 'keys', 'mykey_pub.pem')

SHELL_OFF = 0x5F     # 模数在 shell 里的起始偏移
MOD_LEN = 256        # RSA-2048


def rd(p):
    return io.open(p, encoding='utf-8-sig').read()


def wr(p, s):
    io.open(p, 'w', encoding='utf-8-sig', newline='').write(s)


def decode_array(text, name):
    m = re.search(r'byte\[\] ' + name + r' = new byte\[\] \{(.*?)\};', text, re.S)
    if not m:
        raise SystemExit('src/pdata.cs 里找不到数组 _%s' % name)
    raw = bytes(int(x, 16) for x in re.findall(r'0x([0-9a-fA-F]{2})', m.group(1)))
    return bytes(c ^ ((0x5A + i * 31) & 0xFF) for i, c in enumerate(raw))


def encode_array_cs(name, data, per=16):
    lines = ['    private static readonly byte[] %s = new byte[] {' % name]
    enc = bytes(c ^ ((0x5A + i * 31) & 0xFF) for i, c in enumerate(data))
    for i in range(0, len(enc), per):
        lines.append('        ' + ', '.join('0x%02x' % b for b in enc[i:i + per]) + ',')
    lines.append('    };')
    return '\n'.join(lines)


def modulus_from_pub():
    try:
        from cryptography.hazmat.primitives import serialization
        key = serialization.load_pem_public_key(io.open(PUB, 'rb').read())
        n = key.public_numbers().n
        nb = n.to_bytes((n.bit_length() + 7) // 8, 'big')
    except Exception as e:
        raise SystemExit('读取 %s 失败：%s' % (PUB, e))
    if len(nb) != MOD_LEN:
        raise SystemExit('公钥不是 RSA-2048（N = %d 字节）' % len(nb))
    return nb


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--check', action='store_true', help='只检查，不写文件')
    args = ap.parse_args()

    nb = modulus_from_pub()
    text = rd(PDATA)
    shell = decode_array(text, '_shell')
    if len(shell) != 0x160:
        raise SystemExit('shell 长度异常：%d（预期 352）' % len(shell))

    cur = shell[SHELL_OFF:SHELL_OFF + MOD_LEN]
    want = nb[::-1]          # 小端存放
    if cur == want:
        print('[OK] src/pdata.cs 的 shell 模数已与 keys/mykey_pub.pem 匹配。')
        return 0
    if args.check:
        print('[FAIL] shell 里的模数与当前公钥不一致 —— 请运行：python tools/gen_payload.py')
        return 1

    new_shell = shell[:SHELL_OFF] + want + shell[SHELL_OFF + MOD_LEN:]
    block = encode_array_cs('_shell', new_shell)
    text2, k = re.subn(r'    private static readonly byte\[\] _shell = new byte\[\] \{[\s\S]*?\r?\n    \};', block, text, count=1)
    if k != 1:
        raise SystemExit('替换 _shell 数组失败')
    wr(PDATA, text2)
    print('[OK] 已把 shell 内嵌模数换成当前公钥（%d 字节，偏移 0x%X）。' % (MOD_LEN, SHELL_OFF))
    return 0


if __name__ == '__main__':
    sys.exit(main())
