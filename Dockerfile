FROM python:3.11-slim

WORKDIR /app

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

# rasterio's manylinux wheel bundles GDAL but links against the system libexpat,
# which python:3.11-slim does not ship. Without this, `import rasterio` fails with
# ImportError: libexpat.so.1: cannot open shared object file.
RUN apt-get update     && apt-get install -y --no-install-recommends libexpat1     && rm -rf /var/lib/apt/lists/*

# Create non-root system user
RUN groupadd -g 1000 appgroup && \
    useradd -u 1000 -g appgroup -s /bin/bash -m appuser

COPY requirements.txt .

# torch is pinned in requirements.txt but imported nowhere in app/, scripts/ or tests/.
# On Linux, PyPI's torch wheel drags in the whole CUDA stack (cuBLAS/cuDNN/cuPTI,
# ~6 GB) which this CPU-only, air-gapped backend never touches. Installing the
# CPU-only build first makes the requirements.txt pass see torch==2.5.1 as satisfied.
# ponytail: drop torch from requirements.txt entirely once the team confirms it is unused.
RUN pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu torch==2.5.1 && \
    pip install --no-cache-dir -r requirements.txt

COPY . .

# /app/dbdata must exist in the image so the api_db named volume inherits appuser
# ownership on first init; otherwise Docker creates it root-owned and SQLite fails
# with 'unable to open database file'.
RUN mkdir -p /app/data /app/models /app/indexes /app/dbdata && \
    chown -R appuser:appgroup /app

USER appuser

EXPOSE 8000

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
