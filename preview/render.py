"""Preview of the CAD-independent drafting plan, not a screenshot of AutoCAD.
Usage: python render.py scene.json output.png
"""
import json,sys,math,re
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.patches import Arc,Polygon,Circle
scene=json.load(open(sys.argv[1],encoding='utf-8'))
fig,axes=plt.subplots(1,2,figsize=(17,8),gridspec_kw={'width_ratios':[1.45,1]})
def xy(p):return p['X'],p['Y']
def clean(s):
 s=re.sub(r'\{\\f[^;]+;([^}]*)\}',r'\1',s)
 return s.replace('\\U+25B3','△').replace('%%c','Ø').replace('%%d','°').replace('\\P','\n')
def text(ax,p,s,height=50,color='black',align='left'):
 ax.text(*xy(p),clean(s),fontsize=7 if height<60 else 9,color=color,ha=align,va='center')
for ax,v in zip(axes,scene):
 for p in v['Geometry']:
  a=p['A'];layer=p['Layer'];color='#667580' if layer=='NP_HIDDEN' else '#809097' if layer=='NP_AXIS' else '#16272f'
  if p['Kind']=='line':ax.plot([a['X'],p['B']['X']],[a['Y'],p['B']['Y']],color=color,linewidth=.55)
  elif p['Kind']=='solid':ax.add_patch(Polygon([xy(p[k]) for k in ['A','B','C']],color=color))
  elif p['Kind']=='circle':ax.add_patch(Circle(xy(a),p['Radius'],fill=False,color=color,linewidth=.6))
  elif p['Kind']=='arc':ax.add_patch(Arc(xy(a),p['Radius']*2,p['Radius']*2,theta1=p['Start']*180/math.pi,theta2=p['End']*180/math.pi,color=color,linewidth=.7))
  elif p['Kind']=='text':text(ax,a,p['Text'],p['Height'])
 for d in v['Dimensions']:
  a=d['A'];b=d['B'];line=d['Line'];color='#3865a8'
  if d['Kind']=='aligned':
   dx=b['X']-a['X'];dy=b['Y']-a['Y'];n=math.hypot(dx,dy);nx=-dy/n;ny=dx/n;t=(line['X']-a['X'])*nx+(line['Y']-a['Y'])*ny
   aa=(a['X']+t*nx,a['Y']+t*ny);bb=(b['X']+t*nx,b['Y']+t*ny)
   ax.plot([a['X'],aa[0]],[a['Y'],aa[1]],color=color,lw=.45);ax.plot([b['X'],bb[0]],[b['Y'],bb[1]],color=color,lw=.45);ax.plot([aa[0],bb[0]],[aa[1],bb[1]],color=color,lw=.5)
   for q in [aa,bb]:ax.plot([q[0]-12,q[0]+12],[q[1]-12,q[1]+12],color=color,lw=.7)
   label=(d['Text'] or '<>').replace('<>',f'{n:.1f}'.rstrip('0').rstrip('.'))
  else:
   c=d['Center'];r=math.hypot(line['X']-c['X'],line['Y']-c['Y']);start=math.atan2(a['Y']-c['Y'],a['X']-c['X']);end=math.atan2(b['Y']-c['Y'],b['X']-c['X']);diff=(end-start)%(math.pi*2)
   ax.add_patch(Arc(xy(c),r*2,r*2,theta1=start*180/math.pi,theta2=(start+diff)*180/math.pi,color=color,lw=.5))
   for q in [a,b]:
    angle=math.atan2(q['Y']-c['Y'],q['X']-c['X']);ax.plot([q['X'],c['X']+r*math.cos(angle)],[q['Y'],c['Y']+r*math.sin(angle)],color=color,lw=.4)
   label=d['Text'] or f"L={math.hypot(a['X']-c['X'],a['Y']-c['Y'])*diff:.1f}"
  text(ax,d['TextAt'],label,45,color,'center')
 for lead in v['Leaders']:
  landing=lead['Landing'];end={'X':landing['X']+160,'Y':landing['Y']}
  ax.plot([landing['X'],end['X']],[landing['Y'],end['Y']],color='#328276',lw=.65)
  for tip in lead['Tips']:ax.annotate('',xy(tip),xy(landing),arrowprops={'arrowstyle':'-|>','lw':.65,'color':'#328276'})
  text(ax,{'X':landing['X'],'Y':landing['Y']+35},lead['Text'],45,'#23665d')
 ax.set_aspect('equal');ax.autoscale_view();ax.margins(.15);ax.axis('off')
fig.suptitle('KotNode Project 0.2 — план оформления узла У-01',fontsize=16,x=.04,ha='left',y=.96)
fig.text(.04,.04,'Предпросмотр геометрии и привязок. Размеры — синие, выноски — зеленые.\nОтображение нативных Dimension и MLeader в AutoCAD требует проверки.',fontsize=10,color='#4e626b')
fig.subplots_adjust(left=.03,right=.98,top=.88,bottom=.14,wspace=.15)
fig.savefig(sys.argv[2],dpi=170,facecolor='white')
