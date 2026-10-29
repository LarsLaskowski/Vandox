-- Removes the MariaDB account of the Vandox agent (collector "mariadb"), created by mariadb-user.sql.
--
-- Run this file as the database administrator; uninstall.sh --purge does it for you. It may be run again:
-- DROP USER IF EXISTS does nothing when the account is already gone, and it touches no other account.

DROP USER IF EXISTS 'vandox-agent'@'localhost';
