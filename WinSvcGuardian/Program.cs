using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;

class EnhancedServiceMonitor
{
    private static readonly string configFilePath = "services.txt";
    private static readonly string[] defaultServices =
    {
        /*"Spooler", "wuauserv", "SSDPSRV", "SENS", "Lanmanserver", "hmpalertsvc", "WdiSystemHost", 
        "IntelAudioService", "cplspcon", "DPS", "CryptSvc", "Sophos Endpoint Defense Service", 
        "Sophos File Scanner Service", "Sophos Health Service", "Sophos MCS Agent",
        "Sophos MCS Client", "SntpService",*/
    };

    private static readonly object fileLock = new object();
    private static readonly TimeSpan checkInterval = TimeSpan.FromSeconds(5);
    private static Dictionary<string, ServiceControllerStatus> serviceStatusHistory = new Dictionary<string, ServiceControllerStatus>();

    static async Task Main()
    {
        Console.WriteLine("=== Service Monitor ===");
        Console.WriteLine("Initialisation...");
    
        InitializeConfiguration();
        StopAllConfiguredServicesOnStartup(); // <-- Nouvelle méthode
        await RunContinuousMonitoring();
    }

    private static void InitializeConfiguration()
    {
        lock (fileLock)
        {
            if (!File.Exists(configFilePath))
            {
                Console.WriteLine("Création du fichier de configuration avec services par défaut.");
                File.WriteAllLines(configFilePath, defaultServices);
            }
            else
            {
                var existingServices = ReadConfiguredServices();
                var missingDefaults = defaultServices.Except(existingServices, StringComparer.OrdinalIgnoreCase).ToList();
                
                if (missingDefaults.Any())
                {
                    Console.WriteLine($"Ajout de {missingDefaults.Count} services par défaut manquants.");
                    AppendServicesToConfig(missingDefaults);
                }
            }
        }
    }

    private static void ScanInitialStoppedServices()
    {
        Console.WriteLine("Scan initial des services stoppés...");
        
        var allServices = ServiceController.GetServices();
        var stoppedServices = allServices.Where(s => s.Status != ServiceControllerStatus.Running)
                                       .Select(s => s.ServiceName)
                                       .ToList();

        // Initialiser l'historique des états
        foreach (var service in allServices)
        {
            serviceStatusHistory[service.ServiceName] = service.Status;
        }

        AddNewStoppedServices(stoppedServices, "au démarrage");
    }

    private static async Task RunContinuousMonitoring()
    {
        Console.WriteLine("Démarrage de la surveillance continue...");

        while (true)
        {
            try
            {
                var monitoredServices = ReadConfiguredServices();
                var allServices = ServiceController.GetServices();

                foreach (var service in allServices)
                {
                    // Détection des services récemment arrêtés
                    if (serviceStatusHistory.TryGetValue(service.ServiceName, out var previousStatus))
                    {
                        if (previousStatus == ServiceControllerStatus.Running && 
                            service.Status != ServiceControllerStatus.Running)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Détection: {service.ServiceName} vient d'être arrêté");
                            AddNewStoppedServices(new[] { service.ServiceName }, "arrêté manuellement");
                        }
                    }

                    // Mise à jour de l'historique
                    serviceStatusHistory[service.ServiceName] = service.Status;

                    // Application de la surveillance
                    if (service.Status == ServiceControllerStatus.Running && 
                        monitoredServices.Contains(service.ServiceName, StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Maintien arrêté: {service.ServiceName}");
                            service.Stop();
                            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                            
                            // Vérifier si le service est vraiment arrêté
                            if (service.Status != ServiceControllerStatus.Stopped)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Échec de l'arrêt du service {service.ServiceName} - Retrait du fichier de configuration");
                                RemoveServiceFromConfig(service.ServiceName);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Erreur sur {service.ServiceName}: {ex.Message}");
                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Retrait du service {service.ServiceName} du fichier de configuration");
                            RemoveServiceFromConfig(service.ServiceName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Erreur générale: {ex.Message}");
            }

            await Task.Delay(checkInterval);
        }
    }

    private static void AddNewStoppedServices(IEnumerable<string> services, string context)
    {
        lock (fileLock)
        {
            var existingServices = ReadConfiguredServices();
            var newServices = services.Except(existingServices, StringComparer.OrdinalIgnoreCase)
                                     .Where(s => !string.IsNullOrWhiteSpace(s))
                                     .ToList();

            if (newServices.Any())
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Ajout de {newServices.Count} services ({context}): {string.Join(", ", newServices)}");
                AppendServicesToConfig(newServices);
            }
        }
    }

    private static void RemoveServiceFromConfig(string serviceName)
    {
        lock (fileLock)
        {
            var services = ReadConfiguredServices();
            var updatedServices = services.Where(s => !s.Equals(serviceName, StringComparison.OrdinalIgnoreCase)).ToList();
            File.WriteAllLines(configFilePath, updatedServices);
        }
    }

    private static List<string> ReadConfiguredServices()
    {
        return File.ReadAllLines(configFilePath)
                  .Select(line => line.Trim())
                  .Where(line => !string.IsNullOrWhiteSpace(line))
                  .ToList();
    }

    private static void AppendServicesToConfig(IEnumerable<string> services)
    {
        File.AppendAllLines(configFilePath, services);
    }
    
    private static void StopAllConfiguredServicesOnStartup()
    {
        var monitoredServices = ReadConfiguredServices();
        Console.WriteLine($"Vérification initiale de {monitoredServices.Count} services configurés...");

        foreach (var serviceName in monitoredServices)
        {
            try
            {
                using (var service = new ServiceController(serviceName))
                {
                    if (service.Status == ServiceControllerStatus.Running)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Arrêt initial: {serviceName}");
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                        serviceStatusHistory[serviceName] = ServiceControllerStatus.Stopped;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Échec de l'arrêt initial de {serviceName}: {ex.Message}");
                RemoveServiceFromConfig(serviceName); // Retire le service problématique
            }
        }
    }
}