// Path: src/CarreraApp/Data/ApplicationDbContext.cs
using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using CarreraApp.Models;

namespace CarreraApp.Data
{
    public class ApplicationDbContext : DbContext
    {
        public DbSet<Carrera> Carreras => Set<Carrera>();
        public DbSet<Categoria> Categorias => Set<Categoria>();
        public DbSet<Hit> Hits => Set<Hit>();
        public DbSet<Corredor> Corredores => Set<Corredor>();
        public DbSet<Tiempo> Tiempos => Set<Tiempo>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                // 1. Ruta inmutable y unificada de base de datos
                string dbPath = Helpers.StorageHelper.ObtenerRutaBaseDatos();

                // 2. Conexión SQLite con WAL, Timeouts de espera y claves foráneas activas
                var connectionStringBuilder = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Shared,
                    DefaultTimeout = 5 // Espera hasta 5 segundos si otro hilo está escribiendo
                };

                optionsBuilder.UseSqlite(connectionStringBuilder.ToString());
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Llaves Primarias
            modelBuilder.Entity<Carrera>().HasKey(c => c.ID_Carrera);
            modelBuilder.Entity<Categoria>().HasKey(c => c.ID_Categoria);
            modelBuilder.Entity<Hit>().HasKey(h => h.ID_Hit);
            modelBuilder.Entity<Corredor>().HasKey(c => c.ID_Corredor);
            modelBuilder.Entity<Tiempo>().HasKey(t => t.ID_Tiempo);

            // Relaciones e Integridad Referencial
            modelBuilder.Entity<Corredor>()
                .HasOne(c => c.Categoria)
                .WithMany()
                .HasForeignKey(c => c.ID_Categoria)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Corredor>()
                .HasOne(c => c.Hit)
                .WithMany()
                .HasForeignKey(c => c.ID_Hit)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Corredor>()
                .HasOne(c => c.Carrera)
                .WithMany()
                .HasForeignKey(c => c.ID_Carrera)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Tiempo>()
                .HasOne(t => t.Corredor)
                .WithMany()
                .HasForeignKey(t => t.ID_Corredor)
                .OnDelete(DeleteBehavior.Cascade);

            // ÍNDICES Y RESTRICCIONES ÚNICAS (Crítico para prevenir inconsistencias en concurrencia)
            // Un corredor solo puede tener UN tiempo registrado en una carrera
            modelBuilder.Entity<Tiempo>()
                .HasIndex(t => t.ID_Corredor)
                .IsUnique();

            // Un dorsal no puede repetirse dentro de la misma carrera
            modelBuilder.Entity<Corredor>()
                .HasIndex(c => new { c.ID_Carrera, c.Dorsal })
                .IsUnique();
        }
    }
}