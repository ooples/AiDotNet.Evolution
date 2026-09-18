FROM python@sha256:9d2e5553305c7c7b0097999bb17187c69b921ccd6bc9d40e4bb5ebe652c00285
COPY benchmarks/suite/requirements.txt /requirements.txt
RUN pip install --no-cache-dir --requirement /requirements.txt
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONHASHSEED=0 OMP_NUM_THREADS=1 OPENBLAS_NUM_THREADS=1 MKL_NUM_THREADS=1
USER 65534:65534
WORKDIR /work
ENTRYPOINT ["python", "-I", "-B", "/work/worker.py"]
