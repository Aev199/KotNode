"""Render the production Core result, without AutoCAD; not a host screenshot."""
import json
import math
from pathlib import Path
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.path import Path as MPath
from matplotlib.patches import PathPatch

root=Path(__file__).resolve().parent
scenes=json.loads((root/'scene.json').read_text())

def points(c):
    if not c['Circular']:
        return [(c['X0'],c['Y0']),(c['X1'],c['Y1'])]
    n=max(8,math.ceil(c['Sweep']*60))
    return [(c['X0']+c['Radius']*math.cos(c['Start']+c['Sweep']*i/n),
             c['Y0']+c['Radius']*math.sin(c['Start']+c['Sweep']*i/n)) for i in range(n+1)]

fig,axes=plt.subplots(2,2,figsize=(12,10),facecolor='#f4f6f9')
for ax,scene in zip(axes.flat,scenes):
    r=scene['Result']
    for idx,area in enumerate(r['Sections']):
        vertices=[];codes=[]
        for i,loop in enumerate(area['Loops']):
            ps=[]
            for c in loop: ps.extend(points(c)[:-1])
            if i: ps.reverse()  # holes have opposite winding
            vertices.extend(ps+[ps[0]])
            codes.extend([MPath.MOVETO]+[MPath.LINETO]*(len(ps)-1)+[MPath.CLOSEPOLY])
        ax.add_patch(PathPatch(MPath(vertices,codes),facecolor='#e3e9ef',edgecolor='#718096',hatch='///' if idx%2==0 else '\\\\\\',linewidth=0))
    for hidden in (True,False):
        for c in r['Curves']:
            if c['Hidden']!=hidden:continue
            ps=points(c);ax.plot(*zip(*ps),color='#8294a8' if hidden else '#172a3a',
                linewidth=.8 if hidden else 1.6 if c['Cut'] else 1.1,linestyle=(0,(5,4)) if hidden else '-')
    title=scene['Name']
    if ': ' in title:title=title.replace(': ',':\n',1)
    ax.set_title(title,fontsize=11,pad=14)
    ax.set_aspect('equal');ax.margins(.17);ax.grid(alpha=.15)
    ax.set_xlabel('мм',fontsize=9);ax.set_ylabel('мм',fontsize=9)
fig.suptitle('KotNode · проекции непосредственно из параметров',fontsize=17,y=.98)
fig.text(.5,.025,'Расчёт Core без AutoCAD • сплошные — видимые / срез • штриховые — скрытые\nТруба 100×10, L=200 мм; передняя пластина и пластина внутри полости',ha='center',fontsize=10,color='#44566a')
fig.tight_layout(rect=(0,.065,1,.95))
fig.savefig(root/'preview.png',dpi=160)
