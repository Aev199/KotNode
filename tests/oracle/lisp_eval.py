"""Minimal test harness for pure AutoLISP functions; not an AutoCAD emulator."""
import math,re,json,operator
class Symbol(str):pass
class Lisp:
 def __init__(self,source):
  self.tokens=[t for t in re.findall(r';[^\n]*|"(?:\\.|[^"\\])*"|[()]|\'|[^\s()\'";]+',source) if not t.startswith(';')];self.i=0;self.forms=[]
  while self.i<len(self.tokens):self.forms.append(self.parse())
  self.functions={f[1]:f for f in self.forms if isinstance(f,list) and f and f[0]=='defun'}
  self.scopes=[{'nil':None,'t':True,'pi':math.pi,'kn:unicode':True,'kn:angle-cache':84,':vlax-false':0,':vlax-true':-1}];self.tiles={};self.modes={};self.stubs={}
 def parse(self):
  t=self.tokens[self.i];self.i+=1
  if t=='(':
   a=[]
   while self.tokens[self.i]!=')':a.append(self.parse())
   self.i+=1;return (a[0],a[2]) if len(a)==3 and isinstance(a[1],Symbol) and a[1]==Symbol('.') else a
  if t==')':raise ValueError('Unexpected )')
  if t=="'":return[Symbol('quote'),self.parse()]
  if t.startswith('"'):return json.loads(t)
  try:return float(t) if any(c in t.lower() for c in '.e') else int(t)
  except ValueError:return Symbol(t.lower())
 def truth(self,v):return v is not None and v is not False and v!=[]
 def get(self,k):
  for e in reversed(self.scopes):
   if k in e:return e[k]
  raise ValueError('Unbound '+k)
 def set(self,k,v):
  for e in reversed(self.scopes):
   if k in e:e[k]=v;return v
  self.scopes[0][k]=v;return v
 def seq(self,a):
  v=None
  for q in a:v=self.ev(q)
  return v
 def call(self,f,args):
  if isinstance(f,str) and f in self.stubs:return self.stubs[f](*args)
  if isinstance(f,list) and f[0]=='lambda':spec=f[1];body=f[2:]
  elif f in self.functions:spec=self.functions[f][2];body=self.functions[f][3:]
  else:return self.builtin(f,args)
  slash=spec.index('/') if '/' in spec else len(spec);params=spec[:slash];local=spec[slash+1:]
  assert len(args)==len(params),(f,len(args),len(params))
  self.scopes.append({**{k:None for k in local},**dict(zip(params,args))})
  try:return self.seq(body)
  finally:self.scopes.pop()
 def ev(self,x):
  if isinstance(x,Symbol):return self.get(x)
  if not isinstance(x,list):return x
  if not x:return None
  f=x[0];a=x[1:]
  if f=='defun':self.functions[a[0]]=x;return a[0]
  if f=='quote':return a[0]
  if f=='setq':
   v=None;assert len(a)%2==0,('setq',a)
   for j in range(0,len(a),2):v=self.set(a[j],self.ev(a[j+1]))
   return v
  if f=='if':return self.ev(a[1]) if self.truth(self.ev(a[0])) else (self.ev(a[2]) if len(a)>2 else None)
  if f=='progn':return self.seq(a)
  if f=='cond':
   for branch in a:
    if self.truth(self.ev(branch[0])):return self.seq(branch[1:])
   return None
  if f=='and':
   v=True
   for q in a:
    v=self.ev(q)
    if not self.truth(v):return None
   return v
  if f=='or':
   for q in a:
    v=self.ev(q)
    if self.truth(v):return v
   return None
  if f in ['foreach','vlax-for']:
   v=None
   for item in self.ev(a[1]) or []:self.set(a[0],item);v=self.seq(a[2:])
   return v
  if f=='while':
   v=None;n=0
   while self.truth(self.ev(a[0])):
    v=self.seq(a[1:]);n+=1;assert n<100000
   return v
  if f=='repeat':
   v=None
   for _ in range(int(self.ev(a[0]))):v=self.seq(a[1:])
   return v
  return self.call(f,[self.ev(q) for q in a])
 def builtin(self,f,a):
  if f=='+':return sum(a)
  if f=='-':return -a[0] if len(a)==1 else a[0]-sum(a[1:])
  if f=='*':return math.prod(a)
  if f=='/':return a[0]/math.prod(a[1:])
  if f=='1+':return a[0]+1
  if f=='1-':return a[0]-1
  if f=='atan':return math.atan2(a[0],a[1]) if len(a)>1 else math.atan(a[0])
  if f=='abs':return abs(a[0])
  if f=='vl-remove-if':return [q for q in a[1] if not self.truth(self.call(a[0],[q]))]
  if f in ['sqrt','sin','cos']:return getattr(math,f)(a[0])
  if f in ['<','>','<=','>=','=','/=']:
   op={'<':operator.lt,'>':operator.gt,'<=':operator.le,'>=':operator.ge,'=':operator.eq,'/=':operator.ne}[f];return True if all(op(x,y) for x,y in zip(a,a[1:])) else None
  if f=='not':return True if not self.truth(a[0]) else None
  if f=='nth':return a[1][int(a[0])] if a[1] and int(a[0])<len(a[1]) else None
  if f=='list':return a
  if f=='listp':return True if a[0] is None or isinstance(a[0],list) else None
  if f=='numberp':return True if isinstance(a[0],(int,float)) else None
  if f=='length':return len(a[0] or [])
  if f=='fix':return int(a[0])
  if f=='itoa':return str(int(a[0]))
  if f=='rem':return a[0]%a[1]
  if f=='member':return a[1][a[1].index(a[0]):] if a[0] in a[1] else None
  if f=='append':return sum([q or [] for q in a],[])
  if f=='mapcar':return [self.call(a[0],list(q)) for q in zip(*a[1:])]
  if f=='vl-every':return True if all(self.truth(self.call(a[0],[q])) for q in a[1]) else None
  if f=='apply':return self.call(a[0],a[1])
  if f=='min':return min(a)
  if f=='max':return max(a)
  if f=='reverse':return list(reversed(a[0] or []))
  if f=='last':return a[0][-1:] if a[0] else None
  if f=='cdr':return (a[0][1] if isinstance(a[0],tuple) else a[0][1:]) if a[0] else None
  if f=='cons':return [a[0]]+(a[1] or []) if a[1] is None or isinstance(a[1],list) else (a[0],a[1])
  if f=='vl-sort':
   import functools
   return sorted(a[0] or [],key=functools.cmp_to_key(lambda x,y:-1 if self.truth(self.call(a[1],[x,y])) else (1 if self.truth(self.call(a[1],[y,x])) else 0)))
  if f=='car':return a[0][0] if a[0] else None
  if f=='caddr':return a[0][2]
  if f=='cdddr':return a[0][3:]
  if f=='cddddr':return a[0][4:]
  if f=='assoc':return next((q for q in (a[1] or []) if q[0]==a[0]),None)
  if f=='strcase':return a[0].upper()
  if f=='wcmatch':
   import fnmatch
   return any(fnmatch.fnmatchcase(a[0],p) for p in a[1].split(',')) or None
  if f=='strlen':return len(a[0])
  if f=='subst':return [a[0] if q==a[1] else q for q in a[2]]
  if f=='cadr':return a[0][1]
  if f=='get_tile':return self.tiles[a[0]]
  if f=='set_tile':self.tiles[a[0]]=a[1];return a[1]
  if f=='mode_tile':self.modes[a[0]]=a[1];return None
  if f in ['atoi','distof']:
   try:return int(a[0]) if f=='atoi' else float(a[0])
   except:return None
  if f=='vl-string-search':
   pos=a[1].find(a[0]);return pos if pos>=0 else None
  if f=='vl-string-right-trim':return a[1].rstrip(a[0])
  if f=='rtos':return f'{a[0]:.{a[2]}f}'
  if f=='chr':return chr(a[0])
  if f=='strcat':return ''.join(a)
  if f=='substr':return a[0][a[1]-1:a[1]-1+a[2]] if len(a)>2 else a[0][a[1]-1:]
  raise NotImplementedError((f,a))
