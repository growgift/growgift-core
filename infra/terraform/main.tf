# GrowGift — AWS infrastructure entrypoint (ap-southeast-2 / Sydney).
# Skeleton: modules are outlined with their contracts; internals are filled in
# during infra hardening. State: S3 + DynamoDB locking.

terraform {
  required_version = ">= 1.7"
  required_providers {
    aws = { source = "hashicorp/aws", version = "~> 5.0" }
  }
  backend "s3" {
    bucket         = "growgift-terraform-state"
    key            = "platform/terraform.tfstate"
    region         = "ap-southeast-2"
    dynamodb_table = "growgift-terraform-locks"
    encrypt        = true
  }
}

provider "aws" {
  region = var.region
}

module "network" {
  source   = "./modules/network"
  vpc_cidr = "10.40.0.0/16"
  azs      = ["ap-southeast-2a", "ap-southeast-2b", "ap-southeast-2c"]
}

module "eks" {
  source             = "./modules/eks"
  cluster_name       = "growgift-${var.environment}"
  vpc_id             = module.network.vpc_id
  private_subnet_ids = module.network.private_subnet_ids
}

module "rds" {
  source             = "./modules/rds"          # PostgreSQL 16, Multi-AZ, KMS at rest
  vpc_id             = module.network.vpc_id
  private_subnet_ids = module.network.private_subnet_ids
  database_name      = "growgift"
}

module "msk" {
  source             = "./modules/msk"          # Kafka, TLS in transit, 3 brokers
  vpc_id             = module.network.vpc_id
  private_subnet_ids = module.network.private_subnet_ids
}

module "redis" {
  source             = "./modules/redis"        # ElastiCache, cluster mode, AUTH
  vpc_id             = module.network.vpc_id
  private_subnet_ids = module.network.private_subnet_ids
}
