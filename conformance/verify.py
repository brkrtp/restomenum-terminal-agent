#!/usr/bin/env python3
"""Uygunluk vektörlerinin bozulmadığını doğrular.

Neden ayrı bir betik: bu kontrol CI'da satır içi python olarak yazılmıştı ve YAML blok skalarını
sütun 0'dan başlayarak **kırdı** — workflow hiç çalışmadı. Dosyaya almak hem o hatayı kapatır hem
kontrolü yerelde koşulabilir kılar (`python3 conformance/verify.py`).

Neden bu kontrol var: vektör dosyası boşalırsa iki agent'ın uygunluk testi de "geçer" ama HİÇBİR ŞEY
doğrulanmamış olur — sessizce güven kaybı. Boş vektör, testin kendisinden daha tehlikelidir.
"""
import json
import sys
from pathlib import Path

BEKLENEN = {
    "signing.json": ("vektorler", 6),
    "state-transitions.json": ("vektorler", 30),
    "backoff.json": ("basamaklar_ms", 6),
}

def main() -> int:
    kok = Path(__file__).parent
    hata = 0
    for dosya, (alan, asgari) in BEKLENEN.items():
        yol = kok / dosya
        if not yol.exists():
            print(f"✘ {dosya}: DOSYA YOK")
            hata += 1
            continue
        d = json.loads(yol.read_text(encoding="utf-8"))
        n = len(d.get(alan, []))
        if n < asgari:
            print(f"✘ {dosya}: {alan} {n} kayıt — en az {asgari} olmalı")
            hata += 1
        else:
            print(f"✔ {dosya}: {alan} {n} kayıt")
    if hata:
        print(f"\n{hata} vektör dosyası eksik/bozuk — iki agent da yanlış yere 'geçti' derdi.")
    return 1 if hata else 0

if __name__ == "__main__":
    sys.exit(main())
