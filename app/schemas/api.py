from datetime import date
from pydantic import BaseModel,Field
class IngestRequest(BaseModel): path:str;sensor:str;acquisition_date:date;location_name:str="Unnamed site";source:str="local"
class SearchRequest(BaseModel): query:str;top_k:int=Field(10,ge=1,le=100);sensor:str|None=None
class ImageSearchRequest(BaseModel): observation_id:str;top_k:int=Field(10,ge=1,le=100)
class ChangeRequest(BaseModel): before_observation_id:str;after_observation_id:str
class ReviewRequest(BaseModel): analyst:str;decision:str=Field(pattern="^(confirmed|rejected|needs_review)$");note:str|None=None
class SimilarRequest(BaseModel): location_id:str;top_k:int=Field(10,ge=1,le=100)
