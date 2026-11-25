"""Execute the actual AutoLISP builder with drawing API test doubles.
This verifies layout coordinates, not AutoCAD's rendering or COM implementation.
"""
from pathlib import Path
import math,json
from lisp_eval import Lisp
BASE=Path(__file__).resolve().parent.parent
class BuilderTrace:
 def __init__(self,table_extra=0,source=None):
  self.l=Lisp(source or (BASE/'KotNode.lsp').read_text());self.items=[];self.extra=table_extra
  self.l.scopes[0].update({'kn:dimension-space':'SPACE','kn:pending-dims':None})
  s=self.l.stubs
  s.update({'kn:nonannotative':lambda obj:obj,'kn:ensure-dimstyle':lambda doc:None,'vla-get-name':lambda b:b['name'],'getvar':lambda k:4,'kn:ensure-style':lambda doc:None,'kn:ensure-linetypes':lambda:None,'kn:layer':lambda *a:None,'tblsearch':lambda *a:None,'vla-get-blocks':lambda doc:'BLOCKS','vla-add':lambda *a:{'type':'block','props':{},'name':a[-1]},'vlax-3d-point':lambda v:v,'vla-update':lambda obj:None,'kn:styled':self.style,'kn:poly':self.poly,'kn:table':self.table,'vla-get-height':lambda obj:obj['height']})
  for name,kind in [('line','line'),('mtext','text'),('solid','solid'),('circle','circle'),('arc','arc'),('dimaligned','dim'),('dimarc','arc_dim'),('dim3pointangular','angle_dim')]:
   s['vla-add'+name]=lambda *args,kind=kind:self.add(kind,args[1:],owner=args[0])
  for prop in ['Height','StyleName','TextOverride']:
   s['vla-put-'+prop.lower()]=lambda obj,value,prop=prop.lower():obj['props'].__setitem__(prop,value)
  s['vlax-property-available-p']=lambda obj,prop,modify:not(obj['type']=='angle_dim' and prop in ['primaryunitsprecision','linearscalefactor'])
  s['vlax-put-property']=lambda obj,prop,value:obj['props'].__setitem__(prop,value)
 def add(self,kind,args,owner=None):
  obj={'owner':owner,'type':kind,'args':list(args),'props':{'scalefactor':999.0,'dimline1suppress':-1,'dimline2suppress':-1,'textoverride':' '}}
  self.items.append(obj);return obj
 def style(self,obj,layer):obj['layer']=layer;return obj
 def poly(self,b,points,layer):return self.style(self.add('poly',[points]),layer)
 def table(self,b,x,y,rows,widths,h,values):
  height=(15 if rows==5 else rows*2.5)*h+self.extra
  obj=self.add('table',[x,y,rows,widths,h,values]);obj['height']=height;return obj
 def run(self,D=920,t=10,L=1500,N=4,butt=1,spacing=50,scale=20):
  p=[D,t,L,t,D,10,t,butt,1,13,0,1,1,N,84,1,1,spacing,scale]
  p=self.l.call('kn:normalize',[p]);assert not self.l.truth(self.l.call('kn:valid',[p]))
  self.parts=self.l.call('kn:build-parts',['DOC',p]);return p,self.items

def check(D=920,t=10,L=1500,N=4,butt=1,spacing=50,scale=20):
 tr=BuilderTrace();p,items=tr.run(D,t,L,N,butt,spacing,scale)
 assert len(tr.parts)==6 and len(set(tr.parts[:4]))==4
 dims=[o for o in items if o['type'] in ['dim','arc_dim','angle_dim']]
 assert dims
 for o in dims:
  assert o['owner']=='SPACE', 'Native dimensions must not be owned by a view block'
  assert o['props']['scalefactor']==scale
  assert o['props']['textheight']==2.5
  assert o['props']['stylename']=='KNOT_06_DIM'
  assert o['props']['arrowhead1type']==5 and o['props']['arrowhead2type']==5
  assert o['props']['dimline1suppress']==0 and o['props']['dimline2suppress']==0
 edges=tr.l.call('kn:plate-welds',[p]);assert len(edges)==2*N
 triangles=[o for o in items if o['type']=='solid' and o.get('layer')=='KNOT_Welds']
 assert len(triangles)==2*N
 r=D/2
 for edge in edges:assert abs(math.hypot(edge[2],edge[3])-r)<1e-6
 if N==4:
  rows=tr.l.call('kn:weld-rows',[p]);assert len(rows)==4
  assert all(abs(rows[i][0]-rows[i+1][0])>1e-6 for i in range(3))
 tables=[o for o in items if o['type']=='table'];assert len(tables)==2
 assert all(o['args'][0:2]==[0,0] for o in tables)
 defs=tr.parts[5];assert len(defs)==4+butt
 assert len(next(d for d in defs if d[0]=='weld-main')[2])==2
 return tr,p

if __name__=='__main__':
 n=0
 for D,t in [(426,8),(630,12),(820,8),(920,10)]:
  for N in [1,2,3,4,6,8,12]:
   for scale in [1,10,20,50]:
    for butt in [0,1]:check(D,t,N=N,butt=butt,scale=scale,spacing=(200 if N==2 else 5));n+=1
 report={'version':'0.7','builder_cases':n,'separate_view_and_table_definitions':'PASS','two_weld_edges_on_each_plate':'PASS','section_weld_triangles_2N':'PASS','model_drafting_scale':'PASS','dimensions_owned_by_drawing_space':'PASS','native_AutoCAD_execution':'not available'}
 (BASE/'LAYOUT_CHECKS.json').write_text(json.dumps(report,indent=2));print(json.dumps(report,indent=2))
