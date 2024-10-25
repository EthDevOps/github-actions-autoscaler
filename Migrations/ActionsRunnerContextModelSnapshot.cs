﻿// <auto-generated />
using System;
using GithubActionsOrchestrator.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GithubActionsOrchestrator.Migrations
{
    [DbContext(typeof(ActionsRunnerContext))]
    partial class ActionsRunnerContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "8.0.7")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("GithubActionsOrchestrator.Database.Job", b =>
                {
                    b.Property<int>("JobId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("JobId"));

                    b.Property<DateTime>("CompleteTime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<long>("GithubJobId")
                        .HasColumnType("bigint");

                    b.Property<DateTime>("InProgressTime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("JobUrl")
                        .HasColumnType("text");

                    b.Property<bool>("Orphan")
                        .HasColumnType("boolean");

                    b.Property<string>("Owner")
                        .HasColumnType("text");

                    b.Property<DateTime>("QueueTime")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Repository")
                        .HasColumnType("text");

                    b.Property<string>("RequestedProfile")
                        .HasColumnType("text");

                    b.Property<string>("RequestedSize")
                        .HasColumnType("text");

                    b.Property<int?>("RunnerId")
                        .HasColumnType("integer");

                    b.Property<int>("State")
                        .HasColumnType("integer");

                    b.HasKey("JobId");

                    b.HasIndex("RunnerId")
                        .IsUnique();

                    b.ToTable("Jobs");
                });

            modelBuilder.Entity("GithubActionsOrchestrator.Database.Runner", b =>
                {
                    b.Property<int>("RunnerId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("RunnerId"));

                    b.Property<string>("Arch")
                        .HasColumnType("text");

                    b.Property<string>("Cloud")
                        .HasColumnType("text");

                    b.Property<long>("CloudServerId")
                        .HasColumnType("bigint");

                    b.Property<string>("Hostname")
                        .HasColumnType("text");

                    b.Property<string>("IPv4")
                        .HasColumnType("text");

                    b.Property<bool>("IsCustom")
                        .HasColumnType("boolean");

                    b.Property<bool>("IsOnline")
                        .HasColumnType("boolean");

                    b.Property<int?>("JobId")
                        .HasColumnType("integer");

                    b.Property<string>("Owner")
                        .HasColumnType("text");

                    b.Property<string>("Profile")
                        .HasColumnType("text");

                    b.Property<string>("Size")
                        .HasColumnType("text");

                    b.Property<bool>("StuckJobReplacement")
                        .HasColumnType("boolean");

                    b.HasKey("RunnerId");

                    b.HasIndex("JobId")
                        .IsUnique();

                    b.ToTable("Runners");
                });

            modelBuilder.Entity("GithubActionsOrchestrator.Database.RunnerLifecycle", b =>
                {
                    b.Property<int>("RunnerLifecycleId")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("RunnerLifecycleId"));

                    b.Property<string>("Event")
                        .HasColumnType("text");

                    b.Property<DateTime>("EventTimeUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<int?>("RunnerId")
                        .HasColumnType("integer");

                    b.Property<int>("Status")
                        .HasColumnType("integer");

                    b.HasKey("RunnerLifecycleId");

                    b.HasIndex("RunnerId");

                    b.ToTable("RunnerLifecycles");
                });

            modelBuilder.Entity("GithubActionsOrchestrator.Database.Job", b =>
                {
                    b.HasOne("GithubActionsOrchestrator.Database.Runner", "Runner")
                        .WithOne()
                        .HasForeignKey("GithubActionsOrchestrator.Database.Job", "RunnerId");

                    b.Navigation("Runner");
                });

            modelBuilder.Entity("GithubActionsOrchestrator.Database.Runner", b =>
                {
                    b.HasOne("GithubActionsOrchestrator.Database.Job", "Job")
                        .WithOne()
                        .HasForeignKey("GithubActionsOrchestrator.Database.Runner", "JobId");

                    b.Navigation("Job");
                });

            modelBuilder.Entity("GithubActionsOrchestrator.Database.RunnerLifecycle", b =>
                {
                    b.HasOne("GithubActionsOrchestrator.Database.Runner", null)
                        .WithMany("Lifecycle")
                        .HasForeignKey("RunnerId");
                });

            modelBuilder.Entity("GithubActionsOrchestrator.Database.Runner", b =>
                {
                    b.Navigation("Lifecycle");
                });
#pragma warning restore 612, 618
        }
    }
}
