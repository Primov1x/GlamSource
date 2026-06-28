#!/bin/bash
INPUT=$(cat)
if echo "$INPUT" | grep -q "git commit"; then
  echo '{"hookSpecificOutput":{"hookEventName":"PreToolUse","additionalContext":"REMINDER: Nach git commit direkt git push ausfuehren!"}}'
fi
exit 0
