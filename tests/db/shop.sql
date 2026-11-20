-- A small table for the database browser's tests - the same for PostgreSQL and MariaDB.
CREATE TABLE customers (
    id INTEGER PRIMARY KEY,
    name VARCHAR(80) NOT NULL,
    city VARCHAR(80),
    since DATE
);

INSERT INTO customers (id, name, city, since) VALUES
    (1, 'Anna Berg', 'Hamburg', '2021-03-01'),
    (2, 'Ben Clausen', 'Kiel', '2022-07-15'),
    (3, 'Cora Dahl', NULL, '2023-01-09');

CREATE VIEW customers_in_kiel AS SELECT id, name FROM customers WHERE city = 'Kiel';

-- Enough rows to need more than one page (the browser shows 100 at a time).
CREATE TABLE numbers (n INTEGER PRIMARY KEY);
INSERT INTO numbers (n) SELECT a.x * 10 + b.x + 1
FROM (SELECT 0 AS x UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9 UNION ALL SELECT 10 UNION ALL SELECT 11 UNION ALL SELECT 12 UNION ALL SELECT 13 UNION ALL SELECT 14) a,
     (SELECT 0 AS x UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) b;
