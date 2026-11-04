using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kabuor.Erp.Data.Migrations
{
    /// <summary>The audit trail used to copy secrets into its change records (Daraja keys and passkeys, two-step
    /// secrets and recovery codes, KRA communication keys, webhook signing secrets). New records only say they
    /// changed; this rewrites the old ones the same way, once. Nothing else in the records changes.</summary>
    public partial class ScrubAuditSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "AuditEntries" a
                SET "Changes" = (
                    SELECT jsonb_object_agg(e.key, CASE WHEN e.key IN ('StkConsumerKey', 'StkConsumerSecret', 'StkPasskey', 'TotpSecret', 'RecoveryCodes', 'CmcKey', 'Secret')
                                                     THEN to_jsonb('(changed)'::text) ELSE e.value END)::text
                    FROM jsonb_each(a."Changes"::jsonb) AS e(key, value))
                WHERE a."Changes" LIKE '{%'
                  AND (a."Changes" LIKE '%"StkConsumerKey"%' OR a."Changes" LIKE '%"StkConsumerSecret"%' OR a."Changes" LIKE '%"StkPasskey"%'
                    OR a."Changes" LIKE '%"TotpSecret"%' OR a."Changes" LIKE '%"RecoveryCodes"%' OR a."Changes" LIKE '%"CmcKey"%' OR a."Changes" LIKE '%"Secret"%');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The secrets are gone on purpose; there is nothing to put back.
        }
    }
}
