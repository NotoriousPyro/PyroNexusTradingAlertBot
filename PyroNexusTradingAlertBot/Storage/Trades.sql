CREATE TABLE Trades (
  `buy_amount` varchar(256),
  `buy_currency` varchar(256),
  `sell_amount` varchar(256),
  `sell_currency` varchar(256),
  `fee_amount` varchar(256),
  `fee_currency` varchar(256),
  `type` varchar(256),
  `exchange` varchar(256),
  `group` varchar(256),
  `comment` varchar(256),
  `imported_from` varchar(256),
  `time` datetime,
  `imported_time` datetime,
  `trade_id` varchar(256),
  `is_published` integer NOT NULL DEFAULT 0,
  `is_ignored` integer NOT NULL DEFAULT 0,
  `cointracking_id` integer NOT NULL,
  PRIMARY KEY("cointracking_id" DESC)
) WITHOUT ROWID