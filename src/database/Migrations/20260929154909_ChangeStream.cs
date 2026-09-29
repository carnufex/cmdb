using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cmdb.Database.Migrations
{
    /// <inheritdoc />
    public partial class ChangeStream : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "graph_change",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tx = table.Column<ulong>(type: "xid8", nullable: false, defaultValueSql: "pg_current_xact_id()"),
                    kind = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_graph_change", x => x.id);
                    table.CheckConstraint("ck_graph_change_kind", "kind IN ('equipment', 'cable', 'terminal', 'circuit', 'reload')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_graph_change_tx",
                table: "graph_change",
                column: "tx");

            // Capture (#11): each graph-relevant row change records the key the graph engine must re-read. Bulk
            // loads set cmdb.bulk = on and write a single 'reload' row instead.
            migrationBuilder.Sql("""
                CREATE FUNCTION graph_change_record(tab text, r jsonb) RETURNS void LANGUAGE plpgsql AS $$
                BEGIN
                    CASE tab
                        WHEN 'equipment' THEN
                            INSERT INTO graph_change (kind, key) VALUES ('equipment', (r->>'id')::bigint);
                        WHEN 'port' THEN
                            INSERT INTO graph_change (kind, key) VALUES ('equipment', (r->>'equipment_id')::bigint);
                        WHEN 'cable' THEN
                            INSERT INTO graph_change (kind, key) VALUES ('cable', (r->>'id')::bigint);
                        WHEN 'conductor' THEN
                            INSERT INTO graph_change (kind, key) VALUES ('cable', (r->>'cable_id')::bigint);
                        WHEN 'conductor_end' THEN
                            INSERT INTO graph_change (kind, key)
                            SELECT 'cable', cable_id FROM conductor WHERE id = (r->>'conductor_id')::bigint;
                        WHEN 'connection' THEN
                            INSERT INTO graph_change (kind, key)
                            VALUES ('terminal', (r->>'a_terminal_id')::bigint), ('terminal', (r->>'b_terminal_id')::bigint);
                        WHEN 'circuit' THEN
                            INSERT INTO graph_change (kind, key) VALUES ('circuit', (r->>'id')::bigint);
                        ELSE -- circuit_hop, circuit_dependency, service_circuit
                            INSERT INTO graph_change (kind, key) VALUES ('circuit', (r->>'circuit_id')::bigint);
                    END CASE;
                END $$;

                CREATE FUNCTION graph_change_capture() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF current_setting('cmdb.bulk', true) = 'on' THEN
                        RETURN NULL;
                    END IF;
                    IF TG_OP = 'TRUNCATE' THEN
                        INSERT INTO graph_change (kind, key) VALUES ('reload', 0);
                        RETURN NULL;
                    END IF;
                    IF TG_OP IN ('UPDATE', 'DELETE') THEN
                        PERFORM graph_change_record(TG_TABLE_NAME, to_jsonb(OLD));
                    END IF;
                    IF TG_OP IN ('INSERT', 'UPDATE') THEN
                        PERFORM graph_change_record(TG_TABLE_NAME, to_jsonb(NEW));
                    END IF;
                    RETURN NULL;
                END $$;

                CREATE FUNCTION graph_change_notify() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM pg_notify('graph_change', '');
                    RETURN NULL;
                END $$;

                CREATE TRIGGER graph_change_equipment AFTER INSERT OR DELETE OR UPDATE OF site_id ON equipment
                    FOR EACH ROW EXECUTE FUNCTION graph_change_capture();
                CREATE TRIGGER graph_change_port AFTER INSERT OR DELETE OR UPDATE OF equipment_id, terminal_id ON port
                    FOR EACH ROW EXECUTE FUNCTION graph_change_capture();
                CREATE TRIGGER graph_change_cable AFTER INSERT OR DELETE OR UPDATE OF lifecycle ON cable
                    FOR EACH ROW EXECUTE FUNCTION graph_change_capture();
                CREATE TRIGGER graph_change_conductor AFTER INSERT OR DELETE OR UPDATE OF cable_id ON conductor
                    FOR EACH ROW EXECUTE FUNCTION graph_change_capture();
                CREATE TRIGGER graph_change_conductor_end AFTER INSERT OR DELETE OR UPDATE OF conductor_id, terminal_id ON conductor_end
                    FOR EACH ROW EXECUTE FUNCTION graph_change_capture();
                CREATE TRIGGER graph_change_connection AFTER INSERT OR DELETE OR UPDATE OF a_terminal_id, b_terminal_id, kind, lifecycle, valid_to ON connection
                    FOR EACH ROW EXECUTE FUNCTION graph_change_capture();
                CREATE TRIGGER graph_change_circuit AFTER INSERT OR DELETE OR UPDATE OF layer ON circuit
                    FOR EACH ROW EXECUTE FUNCTION graph_change_capture();
                CREATE TRIGGER graph_change_circuit_hop AFTER INSERT OR DELETE OR UPDATE ON circuit_hop
                    FOR EACH ROW EXECUTE FUNCTION graph_change_capture();
                CREATE TRIGGER graph_change_circuit_dependency AFTER INSERT OR DELETE OR UPDATE ON circuit_dependency
                    FOR EACH ROW EXECUTE FUNCTION graph_change_capture();
                CREATE TRIGGER graph_change_service_circuit AFTER INSERT OR DELETE OR UPDATE ON service_circuit
                    FOR EACH ROW EXECUTE FUNCTION graph_change_capture();
                CREATE TRIGGER graph_change_truncate AFTER TRUNCATE ON connection
                    FOR EACH STATEMENT EXECUTE FUNCTION graph_change_capture();
                CREATE TRIGGER graph_change_notify AFTER INSERT ON graph_change
                    FOR EACH STATEMENT EXECUTE FUNCTION graph_change_notify();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER graph_change_equipment ON equipment;
                DROP TRIGGER graph_change_port ON port;
                DROP TRIGGER graph_change_cable ON cable;
                DROP TRIGGER graph_change_conductor ON conductor;
                DROP TRIGGER graph_change_conductor_end ON conductor_end;
                DROP TRIGGER graph_change_connection ON connection;
                DROP TRIGGER graph_change_circuit ON circuit;
                DROP TRIGGER graph_change_circuit_hop ON circuit_hop;
                DROP TRIGGER graph_change_circuit_dependency ON circuit_dependency;
                DROP TRIGGER graph_change_service_circuit ON service_circuit;
                DROP TRIGGER graph_change_truncate ON connection;
                DROP TRIGGER graph_change_notify ON graph_change;
                DROP FUNCTION graph_change_capture();
                DROP FUNCTION graph_change_notify();
                DROP FUNCTION graph_change_record(text, jsonb);
                """);

            migrationBuilder.DropTable(
                name: "graph_change");
        }
    }
}
