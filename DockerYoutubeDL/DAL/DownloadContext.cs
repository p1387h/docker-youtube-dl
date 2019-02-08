using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DockerYoutubeDL.DAL
{
    public class DownloadContext : DbContext
    {
        public DbSet<HangfireInformation> HangfireInformation { get; set; }
        public DbSet<DownloadTask> DownloadTask { get; set; }
        public DbSet<DownloadResult> DownloadResult { get; set; }

        public DownloadContext()
        {
        }

        public DownloadContext(DbContextOptions options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Configuration of the HangfireInformation table.
            modelBuilder.Entity<HangfireInformation>()
                .HasKey(x => x.Id);
            modelBuilder.Entity<HangfireInformation>()
                .Property(x => x.Id)
                .IsRequired();
            modelBuilder.Entity<HangfireInformation>()
                .Property(x => x.HangfireExecutionType)
                .IsRequired();

            // Configuration of the DownloadTask table.
            modelBuilder.Entity<DownloadTask>()
                .HasKey(x => x.Id);
            modelBuilder.Entity<DownloadTask>()
                .Property(x => x.Id)
                .IsRequired();
            modelBuilder.Entity<DownloadTask>()
                .Property(x => x.Url)
                .IsRequired();
            modelBuilder.Entity<DownloadTask>()
                .Property(x => x.DateAdded)
                .IsRequired();
            modelBuilder.Entity<DownloadTask>()
                .HasMany<DownloadResult>(x => x.DownloadResult)
                .WithOne(x => x.DownloadTask)
                .OnDelete(DeleteBehavior.Cascade);
            modelBuilder.Entity<DownloadTask>()
                .HasMany<HangfireInformation>(x => x.HangfireInformation)
                .WithOne(x => x.DownloadTask)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            // Configuration of the DownloadResult table.
            modelBuilder.Entity<DownloadResult>()
                .HasKey(x => x.Id);
            modelBuilder.Entity<DownloadResult>()
                .Property(x => x.Id)
                .IsRequired();
            modelBuilder.Entity<DownloadResult>()
                .Property(x => x.Index)
                .IsRequired();
        }
    }
}
