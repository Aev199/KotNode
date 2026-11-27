"""Fail CI if a registered command has no labelled UI button, or a button is stale."""
from pathlib import Path
import re
root=Path(__file__).resolve().parents[1]
registered=set()
registrations=[]
for path in (root/'src/AutoCAD').glob('*.cs'):
    registrations.extend(re.findall(r'\[CommandMethod\("([A-Z0-9]+)"',path.read_text(encoding='utf-8')))
registered=set(registrations)
assert len(registrations)==len(registered),'Duplicate registered command names'
text=(root/'src/AutoCAD/ToolCatalog.cs').read_text(encoding='utf-8')
entries=re.findall(r'new ToolAction\("([A-Z0-9]+)","([^"]+)","([^"]+)",(true|false)\)',text)
buttons={row[0] for row in entries}
assert len(entries)==len(buttons),'Duplicate tool buttons'
assert buttons==registered,f'Missing UI buttons: {registered-buttons}; stale UI commands: {buttons-registered}'
assert all(title.strip() and group.strip() for _,group,title,_ in entries),'Unlabelled tool'
print(f'PASS UI command coverage: {len(buttons)} registered commands have labelled buttons')
