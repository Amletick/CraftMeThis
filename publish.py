"""Build CraftMeThis and publish it as a GitHub release.

Usage:  python publish.py [version]      e.g. python publish.py 0.5.1   (no argument = re-publish the csproj version)

Dalamud reads https://github.com/Amletick/CraftMeThis/releases/latest/download/repo.json. A "latest/download"
link redirects straight to the newest release asset, so a new version is visible at once, unlike
raw.githubusercontent.com, which caches files for about five minutes.

The GitHub token is taken from git's credential manager (the same login git push uses).
"""
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.request

OWNER, REPO = "Amletick", "CraftMeThis"
ROOT = os.path.dirname(os.path.abspath(__file__))
CSPROJ = os.path.join(ROOT, "CraftMeThis.csproj")
REPO_JSON = os.path.join(ROOT, "repo.json")
ZIP = os.path.join(ROOT, "bin", "Release", "CraftMeThis", "latest.zip")
DIST = os.path.join(ROOT, "dist", "latest.zip")


def run(*cmd, **kw):
    print("$", " ".join(cmd))
    return subprocess.run(cmd, cwd=ROOT, check=True, text=True, **kw)


def token():
    out = subprocess.run(["git", "credential", "fill"], input="protocol=https\nhost=github.com\n\n",
                         capture_output=True, text=True, check=True,
                         env={**os.environ, "GIT_TERMINAL_PROMPT": "0", "GCM_INTERACTIVE": "never"}).stdout
    for line in out.splitlines():
        if line.startswith("password="):
            return line[len("password="):]
    sys.exit("no GitHub token in git's credential manager")


def api(method, url, tok, body=None, data=None, ctype="application/json"):
    payload = data if data is not None else (json.dumps(body).encode() if body is not None else None)
    req = urllib.request.Request(url, data=payload, method=method, headers={
        "Authorization": f"Bearer {tok}", "Accept": "application/vnd.github+json", "Content-Type": ctype})
    try:
        with urllib.request.urlopen(req) as r:
            raw = r.read()
            return json.loads(raw) if raw else {}
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        sys.exit(f"GitHub {method} {url}: {e.code} {e.read().decode(errors='replace')}")


def main():
    text = open(CSPROJ, encoding="utf-8").read()
    current = re.search(r"<Version>([\d.]+)</Version>", text).group(1)
    version = sys.argv[1] if len(sys.argv) > 1 else current
    if version.count(".") == 2:
        version += ".0"
    if version != current:
        text = text.replace(f"<Version>{current}</Version>", f"<Version>{version}</Version>")
        open(CSPROJ, "w", encoding="utf-8").write(text)
    short = version[:-2] if version.endswith(".0") else version
    tag = f"v{short}"

    env = {**os.environ, "DOTNET_ROOT": os.path.join(os.environ["LOCALAPPDATA"], "Microsoft", "dotnet10")}
    run(os.path.join(env["DOTNET_ROOT"], "dotnet.exe"), "build", "-c", "Release", env=env)
    shutil.copyfile(ZIP, DIST)

    zip_url = f"https://github.com/{OWNER}/{REPO}/releases/download/{tag}/latest.zip"
    entry = json.load(open(REPO_JSON, encoding="utf-8"))
    entry[0].update(AssemblyVersion=version, DownloadLinkInstall=zip_url, DownloadLinkUpdate=zip_url, DownloadLinkTesting=zip_url)
    json.dump(entry, open(REPO_JSON, "w", encoding="utf-8"), ensure_ascii=False, indent=2)

    run("git", "add", "-A")
    if subprocess.run(["git", "diff", "--cached", "--quiet"], cwd=ROOT).returncode != 0:
        run("git", "commit", "-q", "-m", f"Release {tag}\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>")
    run("git", "push", "-q", "origin", "main")

    tok = token()
    base = f"https://api.github.com/repos/{OWNER}/{REPO}"
    rel = api("GET", f"{base}/releases/tags/{tag}", tok)
    if rel is None:
        rel = api("POST", f"{base}/releases", tok, {"tag_name": tag, "target_commitish": "main", "name": f"CraftMeThis {short}",
                                                  "body": f"CraftMeThis {short}", "make_latest": "true"})
    for asset in rel.get("assets", []):
        if asset["name"] in ("latest.zip", "repo.json"):
            api("DELETE", f"{base}/releases/assets/{asset['id']}", tok)
    upload = rel["upload_url"].split("{")[0]
    for name, path, ctype in (("latest.zip", DIST, "application/zip"), ("repo.json", REPO_JSON, "application/json")):
        api("POST", f"{upload}?name={name}", tok, data=open(path, "rb").read(), ctype=ctype)
        print("uploaded", name)
    print(f"published {tag}: https://github.com/{OWNER}/{REPO}/releases/latest/download/repo.json")


if __name__ == "__main__":
    main()
