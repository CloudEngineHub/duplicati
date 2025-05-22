﻿/* This script returns an error message if any encrypted values are found */
SELECT 'Database is encrypted and must be decrypted before it can be downgraded' AS "Message"
FROM Option WHERE Value LIKE 'enc-v1:%'
UNION ALL
SELECT TargetURL
FROM Backup WHERE TargetURL LIKE 'enc-v1:%' LIMIT 1;