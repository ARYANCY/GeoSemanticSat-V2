import numpy as np
class LocalEmbedder:
    """Deterministic visual baseline; not semantic RemoteCLIP."""
    def image(self,x):
        h=np.concatenate([np.histogram(x[min(i,x.shape[0]-1)].ravel(),32,(0,1))[0] for i in range(min(3,x.shape[0]))]).astype('float32');return h/(np.linalg.norm(h)+1e-12)
    def text(self,t): raise RuntimeError("Text search requires staged RemoteCLIP weights and a licensed local adapter; no model download is attempted.")
def embedder(): return LocalEmbedder()
def norm(v):
    v=np.asarray(v,dtype='float32');return v/(np.linalg.norm(v)+1e-12)
