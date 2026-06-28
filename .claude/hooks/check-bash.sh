#!/bin/bash
INPUT=$(cat)
if echo "$INPUT" | grep -q "gh run view" && ! echo "$INPUT" | grep -q "grep"; then
  echo '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","additionalContext":"REGEL: gh run view IMMER mit | grep -i error|fail | head -30 nutzen."}}'
  exit 2
fi
exit 0
