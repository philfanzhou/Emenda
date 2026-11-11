using Microsoft.EntityFrameworkCore;
using Emenda.Database.Entities;

namespace Emenda.Database;

public static class ProductIntakeSchema
{
    public const string Migration = "20261111000000_AddProductIntakeRequests";

    public const string Up = """
        CREATE TABLE product_intake_request (
            request_id character varying(64) NOT NULL,
            caller text NOT NULL,
            student_id character varying(36) NOT NULL,
            payload_hash character varying(64) NOT NULL,
            response jsonb,
            created_at timestamp with time zone NOT NULL,
            CONSTRAINT "PK_product_intake_request" PRIMARY KEY (request_id)
        );
        """;

    public const string Down = """
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM product_intake_request) THEN
                RAISE EXCEPTION 'product_intake_requests_present';
            END IF;
        END $$;
        DROP TABLE product_intake_request;
        """;

    internal static readonly string[] Columns =
    [
        "product_intake_request|request_id|character varying(64)|N",
        "product_intake_request|caller|text|N",
        "product_intake_request|student_id|character varying(36)|N",
        "product_intake_request|payload_hash|character varying(64)|N",
        "product_intake_request|response|jsonb|Y",
        "product_intake_request|created_at|timestamp with time zone|N"
    ];

    internal static void Configure(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ProductIntakeRequestEntity>().Property(e => e.RequestId).ValueGeneratedNever();
}
