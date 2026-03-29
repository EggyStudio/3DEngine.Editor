#!/bin/bash

# BlazorBlueprint Demo Runner
# Usage: ./run-demo.sh

clear

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR" && pwd)"

DEMO_SERVER="$PROJECT_ROOT/demo/BlazorBlueprint.Demo.Server/BlazorBlueprint.Demo.Server.csproj"

run_demo() {
    local project_path=""
    local demo_name=""

    project_path="$DEMO_SERVER"
    demo_name="Blazor Server (InteractiveServer)"

    if [ ! -f "$project_path" ]; then
        echo "Error: Project not found at $project_path"
        exit 1
    fi

    echo ""
    echo "Starting $demo_name..."
    echo "Project: $project_path"
    echo ""
    echo "Press Ctrl+C to stop the server"
    echo "----------------------------------------"
    echo ""

    dotnet run --project "$project_path"
}

run_demo
