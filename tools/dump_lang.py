"""[Godhome] 导出丝之歌本地化表（Resources/Languages/<LANG>_<sheet>）。

文本是 Base64(AES-256-ECB/PKCS7)。密钥在反编译代码 TeamCherry.SharedUtils.Encryption 里，
这里不抄进仓库，运行时从 reference/ 下的文件读（reference/ 不进 git）。

用法: python -I dump_lang.py <resources.assets> <outdir> <keyfile> [LANG ...]
需要: pip install UnityPy pycryptodome
"""
import base64
import os
import sys

import UnityPy
from Crypto.Cipher import AES


def main():
    src, out, keyfile = sys.argv[1], sys.argv[2], sys.argv[3]
    langs = set(sys.argv[4:]) or {"EN", "ZH"}
    key = open(keyfile, encoding="utf-8").read().strip().encode("utf-8")
    os.makedirs(out, exist_ok=True)
    env = UnityPy.load(src)
    count = 0
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        data = obj.read()
        name = data.m_Name
        if name.split("_", 1)[0] not in langs:
            continue
        script = data.m_Script
        if isinstance(script, bytes):
            script = script.decode("utf-8", "replace")
        try:
            raw = AES.new(key, AES.MODE_ECB).decrypt(base64.b64decode(script.strip()))
            txt = raw[: -raw[-1]].decode("utf-8")
        except Exception as e:
            print(f"[Godhome] plain or failed ({e}): {name}")
            txt = script
        with open(os.path.join(out, name + ".txt"), "w", encoding="utf-8") as f:
            f.write(txt)
        count += 1
    print(f"[Godhome] sheets written: {count}")


if __name__ == "__main__":
    main()
