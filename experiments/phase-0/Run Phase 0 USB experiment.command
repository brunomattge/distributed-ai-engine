#!/bin/bash

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
"$SCRIPT_DIR/run-usb-experiment.sh"
RESULT=$?

printf '\n'
if [[ "$RESULT" -eq 0 ]]; then
  echo "Test completed. Results are in the results folder."
else
  echo "The test stopped or failed. Review the step shown above and the logs in results."
fi
read -r -p "Press Enter to close this Terminal window..."
exit "$RESULT"
