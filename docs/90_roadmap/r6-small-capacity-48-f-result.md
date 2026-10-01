# Reduced-capacity F48 observation

Independent population c2-8b49903f4c0a461a84fe, source 5b9dc694354d9dcca9928355a9cedaffdf7860b7, message limit 48 (production 64 unchanged). This never-merge experimental runtime stopped before capacity: 16 scheduled, 4 attempted, 3 accepted, 2 restored, 0 reset and 12 unattempted.

The fourth worker restored its predecessor successfully, then returned agent_missing_tool. Its call records transport success, chat threw and unknown usage. The existing parser emits this diagnostic for a valid no-tool response with finish_reason=stop. This is not a measured capacity event. No raw provider response or private continuation was retained.

Ten dispatched calls include nine with known usage and one with unknown usage. Complete reference cost and actual billing are unknown; the unknown call is not zero. The full USD 12.80 / 4,960-second reservation remains consumed. Capture and cleanup completed, all owned workers exited, private temporary state was deleted, and the credential was absent from outputs.

F remains a failed independent population regardless of later results. It was not retried, did not reset, and supplies no completed capacity path, default-64, production Host, C1 or reliability-rate evidence.
