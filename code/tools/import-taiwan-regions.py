"""Build the Taiwan county/township list used with CWA township forecasts.

Usage: python tools/import-taiwan-regions.py   (requires: pip install opencc)
Townships come from the bundled AreaCity rows under code 71, converted with OpenCC s2tw;
Kinmen and Lienchiang are listed explicitly because AreaCity files them under Fujian.
Names follow the official forms CWA uses as LocationName (臺北市, 后里區, 霧臺鄉).
"""
import json
from pathlib import Path

import opencc

regions = Path(__file__).resolve().parents[1] / "BigBlueFish.Windows/assets/regions"
rows = json.loads((regions / "china-regions.json").read_text(encoding="utf-8"))
convert = opencc.OpenCC("s2tw").convert
# CWA F-D0047 township forecast dataset (next days, 3-hourly) per county.
counties = [
    ("臺北市", "061"), ("新北市", "069"), ("基隆市", "049"), ("桃園市", "005"), ("新竹市", "053"),
    ("新竹縣", "009"), ("苗栗縣", "013"), ("臺中市", "073"), ("彰化縣", "017"), ("南投縣", "021"),
    ("雲林縣", "025"), ("嘉義市", "057"), ("嘉義縣", "029"), ("臺南市", "077"), ("高雄市", "065"),
    ("屏東縣", "033"), ("宜蘭縣", "001"), ("花蓮縣", "041"), ("臺東縣", "037"), ("澎湖縣", "045"),
    ("金門縣", "085"), ("連江縣", "081"),
]
explicit = {
    "金門縣": ["金城鎮", "金湖鎮", "金沙鎮", "金寧鄉", "烈嶼鄉", "烏坵鄉"],
    "連江縣": ["南竿鄉", "北竿鄉", "莒光鄉", "東引鄉"],
}
source = {convert(r["Name"]): r["Code"] for r in rows if r["ParentCode"] == "71"}
# The root stays outside the mainland province list ("0"), which already has its own entry for code 71.
result = [dict(Code="TW", ParentCode="", Level=0, Name="臺灣", Latitude=None, Longitude=None)]
for county, dataset in counties:
    code = "TW-" + dataset
    result.append(dict(Code=code, ParentCode="TW", Level=1, Name=county, Latitude=None, Longitude=None))
    towns = explicit.get(county) or [convert(r["Name"]) for r in rows if r["ParentCode"] == source[county]]
    for index, town in enumerate(towns, 1):
        result.append(dict(Code=f"{code}-{index:02}", ParentCode=code, Level=2, Name=town, Latitude=None, Longitude=None))
(regions / "taiwan-regions.json").write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
print(f"{len(counties)} counties, {sum(r['Level'] == 2 for r in result)} townships")
