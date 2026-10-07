import urllib.request, io, zipfile, sys
url=sys.argv[1]
class R(io.RawIOBase):
    def __init__(s,u):
        s.u=urllib.request.urlopen(urllib.request.Request(u,method='HEAD')).geturl(); s.p=0
        s.n=int(urllib.request.urlopen(urllib.request.Request(s.u,method='HEAD')).headers['Content-Length'])
    def seekable(s): return True
    def readable(s): return True
    def tell(s): return s.p
    def seek(s,o,w=0):
        s.p = o if w==0 else (s.p+o if w==1 else s.n+o); return s.p
    def readinto(s,b):
        if s.p>=s.n: return 0
        e=min(s.p+len(b),s.n)-1
        d=urllib.request.urlopen(urllib.request.Request(s.u,headers={'Range':f'bytes={s.p}-{e}'})).read()
        b[:len(d)]=d; s.p+=len(d); return len(d)
z=zipfile.ZipFile(io.BufferedReader(R(url),buffer_size=8<<20))
if len(sys.argv)==2:
    for i in z.infolist(): print(f"{i.file_size/1e6:9.1f}MB {i.filename}")
else:
    for name in sys.argv[2:]:
        z.extract(name, "."); print("extracted", name)
