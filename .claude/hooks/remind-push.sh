#!/bin/bash
INPUT=$(cat)
if echo "$INPUT" | grep -q "git commit" && ! echo "$INPUT" | grep -q "git push"; then
  echo '{"hookSpecificOutput":{"hookEventName":"PreToolUse","additionalContext":"REMINDER: Nach git commit IMMER direkt git push ausführen!"}}'
fi
exit 0
