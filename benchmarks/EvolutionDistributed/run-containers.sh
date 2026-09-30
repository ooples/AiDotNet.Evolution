#!/usr/bin/env bash
# V1-61 (#174): runs the distributed harness across containers, each with its own network namespace, and writes
# everything verify.py needs into OUT.
#
#   run-containers.sh PUBLISH_DIR OUT
#
# Runs, in order:
#   single  one container: the coordinator with one in-process worker (the single-host baseline)
#   multi   a coordinator and three worker containers; w1 is killed while it holds a lease
#   tp1     throughput with one remote worker and a 1 s evaluator (continuous dispatch)
#   tp4     throughput with four remote workers and a 1 s evaluator (continuous dispatch)
# single and multi use batch dispatch, which commits whole batches and so is unaffected by a lease a dead worker
# holds for seconds. Continuous dispatch keeps workers busier; its independence from such a stall depends on the
# deterministic admission bound from #199, so it is not used for the kill test. tp1 and tp4 still must reach the
# same state: the run's result may not depend on how many workers evaluate it.
# Workers reach the coordinator only over the container network. The share volume carries the bootstrap file
# (certificate fingerprint and compatibility hash), mounted read-only on the workers.
set -euo pipefail

APP=$(realpath "$1")
OUT=$(realpath -m "$2")
IMAGE=${IMAGE:-mcr.microsoft.com/dotnet/runtime:10.0}
EVALUATIONS=${EVALUATIONS:-80}
TOKEN=$(openssl rand -hex 24)
NETWORK=evolution-distributed
mkdir -p "$OUT"
docker network create "$NETWORK" >/dev/null

cleanup() {
  docker ps -aq --filter "label=evolution-distributed" | xargs -r docker rm -f >/dev/null
  docker network rm "$NETWORK" >/dev/null 2>&1 || true
}
trap cleanup EXIT

coordinator() { # name share args...
  local name=$1 share=$2
  shift 2
  mkdir -p "$share"
  docker run -d --name "$name" --label evolution-distributed --network "$NETWORK" \
    -v "$APP:/app:ro" -v "$share:/share" "$IMAGE" \
    dotnet /app/EvolutionDistributed.dll coordinate --share /share --port 7070 --token "$TOKEN" \
    --evaluations "$EVALUATIONS" --out /share/result.json "$@" >/dev/null
}

worker() { # name coordinator share args...
  local name=$1 host=$2 share=$3
  shift 3
  docker run -d --name "$name" --label evolution-distributed --network "$NETWORK" \
    -v "$APP:/app:ro" -v "$share:/share:ro" "$IMAGE" \
    dotnet /app/EvolutionDistributed.dll work --share /share --host "$host" --port 7070 --token "$TOKEN" \
    --worker-id "$name" "$@" >/dev/null
}

finish() { # run coordinator workers...
  local run=$1 coord=$2
  shift 2
  local status
  status=$(docker wait "$coord")
  docker logs "$coord" >"$OUT/$run-coordinator.log" 2>&1
  for name in "$@"; do
    docker wait "$name" >/dev/null 2>&1 || true
    docker logs "$name" >"$OUT/$run-$name.log" 2>&1 || true
  done
  cp "$SHARE_ROOT/$run/result.json" "$OUT/$run.json"
  echo "$run: coordinator exited $status"
  [ "$status" = "0" ]
}

SHARE_ROOT=$(mktemp -d)

# Single-host baseline.
coordinator single-coord "$SHARE_ROOT/single" --local-workers 1
finish single single-coord

# Multi-host with a killed worker. w1 evaluates slowly so the kill lands while it holds a lease.
coordinator multi-coord "$SHARE_ROOT/multi" --wait-workers 3 --lease-ms 2000
worker w1 multi-coord "$SHARE_ROOT/multi" --eval-ms 3000
worker w2 multi-coord "$SHARE_ROOT/multi" --eval-ms 300
worker w3 multi-coord "$SHARE_ROOT/multi" --eval-ms 300
for _ in $(seq 1 600); do
  if docker logs w1 2>/dev/null | grep -q '"event":"claim"'; then break; fi
  sleep 0.1
done
sleep 0.5
docker kill w1 >/dev/null
echo "multi: killed w1"
finish multi multi-coord w1 w2 w3

# Throughput: one remote worker, then four, with a 1 s evaluator.
coordinator tp1-coord "$SHARE_ROOT/tp1" --wait-workers 1 --dispatch continuous
worker tp1-w1 tp1-coord "$SHARE_ROOT/tp1" --eval-ms 1000
finish tp1 tp1-coord tp1-w1

coordinator tp4-coord "$SHARE_ROOT/tp4" --wait-workers 4 --dispatch continuous
for i in 1 2 3 4; do worker "tp4-w$i" tp4-coord "$SHARE_ROOT/tp4" --eval-ms 1000; done
finish tp4 tp4-coord tp4-w1 tp4-w2 tp4-w3 tp4-w4
