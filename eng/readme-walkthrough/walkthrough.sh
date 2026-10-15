#!/bin/bash
# US-24 (#42): times the README's documented path from a clean machine to a first HTML report.
#
# The metric is "clean machine to first report < 10 min via README", and it read "unmeasured"
# because this development host already has the SDK, Python and a prepared virtualenv, so any
# number taken here would describe a warm machine. A container built only from the README's stated
# prerequisite - the .NET SDK - is clean in every dimension the walkthrough actually exercises: no
# NuGet cache, no installed tool, no project, no packages.
#
# No model calls. The quickstart's task is a pure function, so the whole path is free and offline
# after the package restore.
#
#   docker run --rm -v <this-directory>:/input:ro -w /root mcr.microsoft.com/dotnet/sdk:10.0 \
#     bash -c 'mkdir -p /work && bash /input/walkthrough.sh'
#
# Emits STEP|<name>|<elapsed-ms> per step and TOTAL_MS at the end, so a step that regresses can be
# identified rather than only the total.
set -euo pipefail
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
ms() { date +%s%3N; }
T0=$(ms)
mark() { echo "STEP|$1|$(( $(ms) - T0 ))"; }

cd /work
dotnet new console -o quickstart >/dev/null
mark new-console

cd /work/quickstart
dotnet add package AiDotNet.Evolution >/dev/null
mark add-package

cp /input/Program.cs Program.cs
mark write-program

dotnet run > run.out 2>run.err || { echo "RUN FAILED"; tail -40 run.err; exit 1; }
mark dotnet-run
cat run.out

dotnet tool install --global AiDotNet.Evolution.Cli >/dev/null
mark tool-install

export PATH="$PATH:/root/.dotnet/tools"
aidotnet-evolve report trace.jsonl report.html >/dev/null
mark report

echo "TRACE_BYTES|$(stat -c%s trace.jsonl)"
echo "REPORT_BYTES|$(stat -c%s report.html)"
echo "TRACE_RECORDS|$(wc -l < trace.jsonl)"
aidotnet-evolve inspect trace.jsonl
echo "TOTAL_MS|$(( $(ms) - T0 ))"
