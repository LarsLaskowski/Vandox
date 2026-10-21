-- MariaDB account of the Vandox agent (collector "mariadb").
--
-- Run this file ONCE as the database administrator; it may be run again (for example after a Plesk
-- update dropped the user): CREATE OR REPLACE USER resets the account to exactly the state below.
--
--   as root on a plain MariaDB server:  mariadb < mariadb-user.sql
--   on a Plesk server:                  plesk db < mariadb-user.sql
--
-- The account `vandox-agent`@`localhost` is authenticated by the operating system user of the
-- connecting process over the local socket (unix_socket): it has no password, so none is stored or
-- sent. It may hold at most 2 connections and has only the PROCESS privilege, which shows the
-- statements of all connections in SHOW PROCESSLIST; it grants no access to tables or their data.
CREATE OR REPLACE USER 'vandox-agent'@'localhost' IDENTIFIED VIA unix_socket WITH MAX_USER_CONNECTIONS 2;
GRANT PROCESS ON *.* TO 'vandox-agent'@'localhost';
