# Trims the vendor indexes downloaded by the M10 spike into small test fixtures for checkpoint 2.
# Run with any Python 3: python make_fixtures.py
import json, pathlib, shutil

here = pathlib.Path(__file__).parent
data = here / "data"
out = here.parent.parent / "tests" / "Tack.Tests" / "Fixtures" / "Installs"
out.mkdir(parents=True, exist_ok=True)

# Node: the newest release of each major from 16 up, plus a few older 24.x/20.x so prefix and LTS resolution
# have more than one candidate. 18.x has no arm64 zip; 26.x has no corepack.
node = json.loads((data / "node-index.json").read_text(encoding="utf-8"))
keep, seen = [], set()
for e in node:
    major = int(e["version"].lstrip("v").split(".")[0])
    if major >= 16 and major not in seen:
        seen.add(major); keep.append(e)
keep += [e for e in node if e["version"] in ("v24.20.0", "v24.0.0", "v20.11.1", "v20.9.0")]
keep.sort(key=lambda e: [int(x) for x in e["version"].lstrip("v").split(".")], reverse=True)
with open(out / "node-index.json", "w", encoding="utf-8", newline="\n") as f:
    f.write("[\n" + ",\n".join(json.dumps(e, separators=(",", ":")) for e in keep) + "\n]\n")

shutil.copyfile(here / "downloads" / "node-24.21.0-SHASUMS256.txt", out / "node-24.21.0-SHASUMS256.txt")

# Python: two pages linked by "next", like the real index. Page 1 keeps every variant of a few versions (core,
# free-threaded, embed, test; 32/64/arm64) plus a pre-release; page 2 keeps older zips and a hashless NuGet entry.
pages = [json.loads((data / n).read_text(encoding="utf-8"))["versions"]
         for n in ("python-index-windows.json", "python-page1.json", "python-page2.json")]
everything = [e for p in pages for e in p]
def pick(versions):
    return [e for e in everything if e["sort-version"] in versions]
page1 = pick({"3.14.8", "3.15.0rc2", "3.13.16"})
page2 = pick({"3.12.10", "3.12.9", "3.11.0"}) + [
    e for e in everything if e["sort-version"] == "3.10.11" and e["id"] == "pythoncore-3.10-64"]
for name, versions, nxt in (("python-index-windows.json", page1, "python-index-page2.json"),
                            ("python-index-page2.json", page2, None)):
    doc = {"versions": versions}
    if nxt: doc["next"] = nxt
    with open(out / name, "w", encoding="utf-8", newline="\n") as f:
        json.dump(doc, f, indent=1); f.write("\n")

for p in sorted(out.iterdir()):
    print(f"{p.stat().st_size:>8}  {p.name}")
