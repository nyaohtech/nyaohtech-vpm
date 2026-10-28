"""Release assets の download_count を集めて Website/stats.json に書く。"""
import json, os, re, sys, urllib.request

# 詰め合わせを入れると中のツールも同時にダウンロードされる。二重に数えないよう、累計からは除く
BUNDLE = 'com.nyaohtech.avatorsupports'
repo = json.load(open('source.json'))['repo']
req = urllib.request.Request(f'https://api.github.com/repos/{repo}/releases?per_page=100',
                             headers={'Accept': 'application/vnd.github+json'})
if os.environ.get('GH_TOKEN'):
    req.add_header('Authorization', 'Bearer ' + os.environ['GH_TOKEN'])

packages = {}
for rel in json.load(urllib.request.urlopen(req)):
    for a in rel['assets']:
        m = re.match(r'(com\.nyaohtech\..+)-\d[\d.]*\.zip$', a['name'])
        if m:
            packages[m[1]] = packages.get(m[1], 0) + a['download_count']

out = {'updated': __import__('datetime').datetime.now(__import__('datetime').timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
       'total': sum(n for k, n in packages.items() if k != BUNDLE), 'packages': packages}
json.dump(out, open('Website/stats.json', 'w'), indent=1)
print(out)
