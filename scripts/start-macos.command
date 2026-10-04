#!/bin/sh
cd "$(dirname "$0")" || exit 1
echo "Aria2Fast Web: http://localhost:8080"
echo "Initial password: data/initial-password.txt"
exec ./Aria2Fast.Web
