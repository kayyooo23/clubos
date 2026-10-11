"""Как получены файлы Manrope-*.ttf в этой папке (WPF не умеет переменные шрифты, ему нужны отдельные файлы на каждое начертание).

Исходник: переменный шрифт Manrope из репозитория Google Fonts (ofl/manrope, лицензия OFL, текст рядом: OFL.txt).
Запуск:  pip install fonttools  и  python make-static-fonts.py "путь/к/Manrope[wght].ttf" .
Каждое начертание получает имена по схеме «Manrope» (семейство, nameID 16) и «Regular/Medium/SemiBold/Bold» (подсемейство, nameID 17),
а в Win32-имени (nameID 1) Medium и SemiBold идут отдельными семействами: так WPF подбирает нужный вес в одном семействе «Manrope»
по FontWeight и не подмешивает искусственный полужирный.
"""
import sys
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

FAMILY = "Manrope"
WEIGHTS = {400: "Regular", 500: "Medium", 600: "SemiBold", 700: "Bold"}


def main(source: str, out_dir: str) -> None:
    for weight, style in WEIGHTS.items():
        instance = instantiateVariableFont(TTFont(source), {"wght": weight}, inplace=False)
        name = instance["name"]
        for record in list(name.names):
            if record.nameID in (1, 2, 3, 4, 6, 16, 17, 21, 22, 25):
                name.removeNames(nameID=record.nameID)
        legacy = FAMILY if weight in (400, 700) else f"{FAMILY} {style}"
        sub = "Bold" if weight == 700 else "Regular"
        full = f"{FAMILY} {style}"
        for name_id, value in ((1, legacy), (2, sub), (3, f"{full};ClubOS"), (4, full), (6, f"{FAMILY}-{style}"), (16, FAMILY), (17, style)):
            name.setName(value, name_id, 3, 1, 0x409)
        os2 = instance["OS/2"]
        os2.usWeightClass = weight
        os2.fsSelection = (os2.fsSelection & ~0b1100001) | (0b100000 if weight == 700 else 0b1000000)
        instance["head"].macStyle = 1 if weight == 700 else 0
        instance.save(f"{out_dir}/{FAMILY}-{style}.ttf")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else ".")
