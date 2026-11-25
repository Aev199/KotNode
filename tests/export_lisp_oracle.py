from pathlib import Path
import sys,math,json,hashlib
root=Path(__file__).resolve().parent
sys.path.insert(0,str(root/'oracle'))
from test_layout import BuilderTrace
from lisp_eval import Lisp
source=(root/'oracle/KotNode-0.7-readable.lsp').read_text()
l=Lisp(source)
cases=[]
for D,t in [(426,8),(630,12),(820,8),(920,10)]:
 for N in [1,2,3,4,6,8,12]:
  for scale in [1,10,20,50]:
   # Numeric oracle covers all 112 geometry/count/scale combinations.
   spacing=200 if N==2 else 5
   p=[D,t,1500,t,D,10,9,0,1,13,0,1,1,N,84,1,1,spacing,scale]
   p=l.call('kn:normalize',[p])
   assert not l.truth(l.call('kn:valid',[p]))
   case=dict(D=D,T=t,L=1500,N=N,Spacing=spacing,Scale=scale,Angle=p[14],Edges=l.call('kn:plate-welds',[p]),Rows=l.call('kn:weld-rows',[p]),Leaders=l.call('kn:leader-defs',[p]),Sectors=l.call('kn:sectors',[p]))
   # Full builder oracle: multiple sizes, 1..12 plates, with/without butt weld.
   if scale==20:
    layouts=[]
    for butt in [0,1]:
     tr=BuilderTrace(source=source);p,items=tr.run(D,t,1500,N,butt,spacing,scale)
     pending=tr.l.get('kn:pending-dims')
     dims=[dict(Role=role,Kind=obj['type'],Args=obj['args'],Props=obj['props']) for role,obj in pending]
     layouts.append(dict(Butt=bool(butt),Dimensions=dims,Leaders=tr.parts[5],MainOffset=tr.parts[4][1][1]))
    case['Layouts']=layouts
   cases.append(case)
result=dict(SourceSHA256=hashlib.sha256(source.encode()).hexdigest(),Cases=cases)
(root/'fixtures/lisp07.json').write_text(json.dumps(result,ensure_ascii=False,separators=(',',':')))
print('Exported',len(cases),'numeric cases and',sum(len(c.get('Layouts',[])) for c in cases),'full layouts')
