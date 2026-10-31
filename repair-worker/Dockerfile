FROM python:3.12-slim-bookworm
RUN apt-get update && apt-get install -y --no-install-recommends docker.io git ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /worker
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt
COPY *.py ./
COPY Dockerfile.codex /worker/toolchain/Dockerfile
EXPOSE 8766
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8766", "--no-access-log"]
