#!/bin/bash
export GG_CODE_PEPPER=dev-pepper-change-me
export STRIPE_WEBHOOK_SECRET=whsec_dev
export ASPNETCORE_ENVIRONMENT=Development

mkdir -p logs

dotnet run --project src/Services/Identity/GrowGift.Identity       --urls http://localhost:5001 > logs/identity.log    2>&1 &
dotnet run --project src/Services/Catalog/GrowGift.Catalog         --urls http://localhost:5002 > logs/catalog.log     2>&1 &
dotnet run --project src/Services/Gifting/GrowGift.Gifting         --urls http://localhost:5003 > logs/gifting.log     2>&1 &
dotnet run --project src/Services/Brokerage/GrowGift.Brokerage     --urls http://localhost:5004 > logs/brokerage.log   2>&1 &
dotnet run --project src/Services/Family/GrowGift.Family           --urls http://localhost:5005 > logs/family.log      2>&1 &
dotnet run --project src/Services/Fulfillment/GrowGift.Fulfillment --urls http://localhost:5006 > logs/fulfillment.log 2>&1 &
dotnet run --project src/Services/Notifications/GrowGift.Notifications --urls http://localhost:5007 > logs/notifications.log 2>&1 &
dotnet run --project src/Gateway/GrowGift.Gateway                  --urls http://localhost:8080 > logs/gateway.log     2>&1 &

echo "Starting... give it ~20 seconds, then check:"
echo "  curl http://localhost:5001/healthz"
wait
