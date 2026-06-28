#!/bin/bash
INPUT=$(cat)
if echo "$INPUT" | grep -qi "fehler\|error\|fix\|problem" && ! echo "$INPUT" | grep -q "gh run"; then
  echo '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","additionalContext":"REGEL: Fehler IMMER erst mit gh run view analysieren bevor Änderungen gemacht werden."}}'
  exit 2
fi
exit 0
