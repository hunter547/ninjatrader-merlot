.PHONY: setup up up-native up-auth down

setup:
	mkdir -p ninjatrader-data screenshots

up: setup
	docker compose up -d ninjatrader

up-native: setup
	docker compose up -d ninjatrader-native

up-auth: setup
	docker compose up -d ninjatrader-auth

down:
	docker compose down
