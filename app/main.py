from pathlib import Path
import numpy as np, rasterio
from shapely.geometry import box
from fastapi import FastAPI,Depends,HTTPException
from app.core.config import settings
from app.db.session import Base,engine,get_db
from app.models.entities import *
from app.schemas.api import *
from app.services.embeddings.service import embedder,norm
app=FastAPI(title='SIH 26227 Offline Satellite Intelligence API',version='0.1.0')
@app.on_event('startup')
def boot(): Base.metadata.create_all(engine)
@app.get('/health')
async def health(): return {'status':'ok','offline_mode':settings.offline_mode}
@app.get('/system/status')
async def status(db=Depends(get_db)): return {'database':'connected','observations':db.query(Observation).count(),'embeddings':db.query(Embedding).count(),'runtime_network':False,'semantic_model':'RemoteCLIP local adapter required for text search'}
@app.post('/api/v1/ingest',status_code=201)
async def ingest(r:IngestRequest,db=Depends(get_db)):
 try:
  root=settings.data_root.resolve();p=(root/r.path).resolve()
  if root not in p.parents or p.suffix.lower() not in {'.tif','.tiff'} or not p.is_file(): raise ValueError('Valid GeoTIFF path under DATA_ROOT required')
  run=ProcessingRun(operation='ingest',status='running',provenance={'input':r.path});db.add(run);db.flush()
  with rasterio.open(p) as ds:
   if not ds.crs or ds.count<1 or ds.width*ds.height>settings.max_ingest_raster_pixels: raise ValueError('Invalid raster CRS/bands/size')
   a=ds.read(out_shape=(min(ds.count,3),min(256,ds.height),min(256,ds.width)),masked=True).filled(0).astype('float32');a=(a-a.min())/(a.max()-a.min()+1e-6);fp=box(*ds.bounds).wkt
   loc=db.query(Location).filter_by(name=r.location_name).first()
   if not loc:
    loc=Location(name=r.location_name,geometry_wkt=fp);db.add(loc);db.flush()
   src=db.query(SatelliteSource).filter_by(name=r.source).first() or SatelliteSource(name=r.source);db.add(src);db.flush()
   o=Observation(location_id=loc.id,source_id=src.id,acquisition_date=r.acquisition_date,sensor=r.sensor,raster_path=str(p.relative_to(root)),footprint_wkt=fp,quality_score=1.,metadata_json={'crs':str(ds.crs),'shape':[ds.height,ds.width],'bands':ds.count,'preprocessing':'minmax-v1'});db.add(o);db.flush();db.add(Embedding(observation_id=o.id,vector=embedder().image(a).tolist(),model_name='histogram-baseline',model_version='v1',run_id=run.id))
  run.status='completed';db.commit();return {'job_id':run.id,'status':run.status,'observation_id':o.id,'location_id':o.location_id}
 except (ValueError,FileNotFoundError) as e: raise HTTPException(400,str(e))
def results(db,v,k,sensor=None,exclude=None):
 z=[]
 for e,o in db.query(Embedding,Observation).join(Observation).all():
  if (sensor and sensor!=o.sensor) or o.id==exclude: continue
  z.append((float(np.dot(norm(v),norm(e.vector))),o))
 return [{'observation_id':o.id,'location_id':o.location_id,'score':round(s,6),'sensor':o.sensor,'acquisition_date':o.acquisition_date} for s,o in sorted(z,reverse=True,key=lambda x:x[0])[:k]]
@app.get('/api/v1/ingest/{job_id}')
async def job(job_id:str,db=Depends(get_db)):
 x=db.get(ProcessingRun,job_id)
 if not x: raise HTTPException(404,'Job not found')
 return {'id':x.id,'status':x.status,'operation':x.operation,'provenance':x.provenance}
@app.get('/api/v1/observations')
async def observations(sensor:str|None=None,db=Depends(get_db)):
 q=db.query(Observation);q=q.filter_by(sensor=sensor) if sensor else q
 return [{'id':x.id,'location_id':x.location_id,'date':x.acquisition_date,'sensor':x.sensor,'quality':x.quality_score} for x in q]
@app.post('/api/v1/search/image')
async def image_search(r:ImageSearchRequest,db=Depends(get_db)):
 e=db.query(Embedding).filter_by(observation_id=r.observation_id).first()
 if not e: raise HTTPException(404,'Observation has no embedding')
 return {'results':results(db,e.vector,r.top_k,exclude=r.observation_id)}
@app.post('/api/v1/search/text')
@app.post('/api/v1/search/hybrid')
async def text_search(r:SearchRequest,db=Depends(get_db)):
 raise HTTPException(503,'RemoteCLIP local weights and adapter are required for text retrieval; runtime downloads are disabled.')
@app.get('/api/v1/locations/{location_id}')
async def location(location_id:str,db=Depends(get_db)):
 x=db.get(Location,location_id)
 if not x: raise HTTPException(404,'Location not found')
 return {'id':x.id,'name':x.name,'geometry_wkt':x.geometry_wkt,'observations':db.query(Observation).filter_by(location_id=x.id).count()}
@app.get('/api/v1/locations/{location_id}/timeline')
async def timeline(location_id:str,db=Depends(get_db)): return [{'id':x.id,'date':x.acquisition_date,'sensor':x.sensor,'quality':x.quality_score} for x in db.query(Observation).filter_by(location_id=location_id).order_by(Observation.acquisition_date)]
@app.post('/api/v1/change/analyze',status_code=201)
async def analyze(r:ChangeRequest,db=Depends(get_db)):
 a=db.get(Observation,r.before_observation_id);b=db.get(Observation,r.after_observation_id)
 if not a or not b or a.location_id!=b.location_id: raise HTTPException(400,'Two observations from the same location are required')
 try:
  def read(o):
   with rasterio.open(settings.data_root/o.raster_path) as d:return d.read(1,out_shape=(256,256),masked=True).filled(0).astype('float32')
  x,y=read(a),read(b);x=(x-x.mean())/(x.std()+1e-6);y=(y-y.mean())/(y.std()+1e-6);score=float(np.mean(np.abs(x-y)));q=min(a.quality_score,b.quality_score)
  run=ProcessingRun(operation='change_analysis',status='completed',provenance={'algorithm':'normalized-absolute-difference-v1','warning':'baseline score only; class requires trained change model'});db.add(run);db.flush();e=ChangeEvent(location_id=a.location_id,before_observation_id=a.id,after_observation_id=b.id,change_class='NO_CHANGE' if score<.25 else 'OTHER',confidence=min(.95,score/(score+1)*q),evidence={'spectral_difference':score,'quality_factor':q,'false_alarm_risk':1-q,'mask_available':False},run_id=run.id);db.add(e);db.commit();return {'change_id':e.id,'class':e.change_class,'confidence':e.confidence,'evidence':e.evidence}
 except Exception as e: raise HTTPException(400,str(e))
@app.get('/api/v1/change/{change_id}')
async def change(change_id:str,db=Depends(get_db)):
 x=db.get(ChangeEvent,change_id)
 if not x: raise HTTPException(404,'Change event not found')
 return {'id':x.id,'class':x.change_class,'confidence':x.confidence,'evidence':x.evidence}
@app.post('/api/v1/change/{change_id}/review',status_code=201)
async def review(change_id:str,r:ReviewRequest,db=Depends(get_db)):
 if not db.get(ChangeEvent,change_id): raise HTTPException(404,'Change event not found')
 x=AnalystReview(change_event_id=change_id,**r.model_dump());db.add(x);db.commit();return {'review_id':x.id}
@app.get('/api/v1/reviews')
async def reviews(db=Depends(get_db)): return [{'id':x.id,'change_id':x.change_event_id,'decision':x.decision,'analyst':x.analyst} for x in db.query(AnalystReview)]
@app.post('/api/v1/similar-locations')
async def similar(r:SimilarRequest,db=Depends(get_db)):
 o=db.query(Observation).filter_by(location_id=r.location_id).order_by(Observation.acquisition_date.desc()).first()
 if not o: raise HTTPException(404,'Location has no observations')
 e=db.query(Embedding).filter_by(observation_id=o.id).first();return {'results':results(db,e.vector,r.top_k,exclude=o.id)}
@app.get('/api/v1/processing/{run_id}')
async def processing(run_id:str,db=Depends(get_db)): return await job(run_id,db)
@app.get('/api/v1/change/{change_id}/provenance')
async def provenance(change_id:str,db=Depends(get_db)):
 x=db.get(ChangeEvent,change_id)
 if not x: raise HTTPException(404,'Change event not found')
 run=db.get(ProcessingRun,x.run_id);return {'change_id':x.id,'run':run.provenance,'before':x.before_observation_id,'after':x.after_observation_id,'evidence':x.evidence}
