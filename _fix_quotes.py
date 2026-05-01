import re

path = r'x:\Repos\Animarr\src\Animarr.Web\Components\Explorer\FolderEditPanel.razor'
content = open(path, encoding='utf-8').read()

# Fix doubled quotes: L[""key""] → L["key"]
fixed = re.sub(r'L\[""\s*([^"]+?)\s*""\]', lambda m: f'L["{m.group(1)}"]', content)
# Fix OnClick ApplyManualIdAsync(""source"") → ApplyManualIdAsync("source")
fixed = re.sub(r'ApplyManualIdAsync\(""\s*([^"]+?)\s*""\)', lambda m: f'ApplyManualIdAsync("{m.group(1)}")', fixed)

changed = sum(1 for a, b in zip(content.split('\n'), fixed.split('\n')) if a != b)
print(f'Lines changed: {changed}')
open(path, 'w', encoding='utf-8').write(fixed)
print('done')
