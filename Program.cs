using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ScottPlot;

namespace Lab08
{
    public class ProcEventArgs : EventArgs
    {
        public int Id { get; set; }
    }

    struct PoolRecord
    {
        public Thread Thread;
        public bool InUse;
    }

    class Server
    {
        private readonly PoolRecord[] _pool;
        private readonly object _threadLock = new object();
        private readonly Random _rng = new Random();

        public int RequestCount { get; private set; }
        public int ProcessedCount { get; private set; }
        public int RejectedCount { get; private set; }

        private int _activeChannels;
        private DateTime _lastStateChange;
        private double _idleTime;
        private double _busyTime;

        private readonly double _serviceRate;

        public Server(int channels, double serviceRate)
        {
            _pool = new PoolRecord[channels];
            _serviceRate = serviceRate;
            _lastStateChange = DateTime.Now;
        }

        private void UpdateState(int delta)
        {
            DateTime now = DateTime.Now;
            double elapsed = (now - _lastStateChange).TotalSeconds;
            _lastStateChange = now;

            if (_activeChannels == 0)
            {
                _idleTime += elapsed;
            }
            else
            {
                _busyTime += elapsed * _activeChannels;
            }

            _activeChannels += delta;
            if (_activeChannels < 0) _activeChannels = 0;
        }

        private void IncrementActive() => UpdateState(+1);
        private void DecrementActive() => UpdateState(-1);

        public void Proc(object sender, ProcEventArgs e)
        {
            lock (_threadLock)
            {
                RequestCount++;
                Console.WriteLine($"Заявка #{e.Id} поступила");

                for (int i = 0; i < _pool.Length; i++)
                {
                    if (!_pool[i].InUse)
                    {
                        _pool[i].InUse = true;
                        _pool[i].Thread = new Thread(Answer);
                        _pool[i].Thread.Start(e.Id);
                        ProcessedCount++;
                        IncrementActive();
                        Console.WriteLine($"Заявка #{e.Id} принята на канал {i}");
                        return;
                    }
                }
                RejectedCount++;
                Console.WriteLine($"Заявка #{e.Id} отклонена (все каналы заняты)");
            }
        }

        private void Answer(object arg)
        {
            int id = (int)arg;
            double serviceTimeSec = ExponentialRandom(1.0 / _serviceRate);
            Thread.Sleep((int)(serviceTimeSec * 1000));

            lock (_threadLock)
            {
                for (int i = 0; i < _pool.Length; i++)
                    if (_pool[i].Thread == Thread.CurrentThread)
                    {
                        _pool[i].InUse = false;
                        Console.WriteLine($"Канал {i} освободился после заявки #{id}");
                        break;
                    }
                DecrementActive();
            }
        }

        private double ExponentialRandom(double mean)
        {
            double u = _rng.NextDouble();
            return -mean * Math.Log(1 - u);
        }

        public void FinalizeTime()
        {
            lock (_threadLock)
            {
                UpdateState(0);
            }
        }

        public double TotalTime => _idleTime + _busyTime / Math.Max(1, _activeChannels + 1);

        public double IdleTime => _idleTime;
        public double BusyTime => _busyTime;

        public double AvgBusyChannels => _busyTime / TotalTime;

        public double IdleProbability => _idleTime / TotalTime;
    }

    class Client
    {
        private readonly Server _server;
        public event EventHandler<ProcEventArgs> Request;

        public Client(Server server)
        {
            _server = server;
            Request += _server.Proc;
        }

        public void Send(int id)
        {
            OnRequest(new ProcEventArgs { Id = id });
        }

        protected virtual void OnRequest(ProcEventArgs e)
        {
            Request?.Invoke(this, e);
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            const int n = 5;
            const double mu = 2.0;
            const int totalRequests = 100;

            double[] lambdas = { 2.0, 4.0, 6.0, 8.0, 10.0, 12.0, 14.0, 16.0, 18.0, 20.0 };

            Directory.CreateDirectory("result");

            var lambdaVals = new List<double>();
            var expP0 = new List<double>();
            var theoP0 = new List<double>();
            var expPreject = new List<double>();
            var theoPreject = new List<double>();
            var expQ = new List<double>();
            var theoQ = new List<double>();
            var expA = new List<double>();
            var theoA = new List<double>();
            var expK = new List<double>();
            var theoK = new List<double>();

            using (var writer = new StreamWriter("data.txt", false, System.Text.Encoding.UTF8))
            {
                writer.WriteLine($"Моделирование СМО, каналов = {n}, μ = {mu}");
                writer.WriteLine($"{"λ",-6} {"P0_эксп",-8} {"P0_теор",-8} {"Pотк_эксп",-9} {"Pотк_теор",-9} {"Q_эксп",-7} {"Q_теор",-7} {"A_эксп",-7} {"A_теор",-7} {"k_эксп",-7} {"k_теор",-7}");

                foreach (double lambda in lambdas)
                {
                    Console.WriteLine($"\n--- lambda = {lambda} заявок/с ---");
                    var stats = RunSingleExperiment(n, mu, lambda, totalRequests);
                    double rho = lambda / mu;

                    double[] fact = new double[n + 1];
                    fact[0] = 1;
                    for (int i = 1; i <= n; i++) fact[i] = fact[i - 1] * i;
                    double sum = 0;
                    for (int i = 0; i <= n; i++) sum += Math.Pow(rho, i) / fact[i];
                    double P0_theo = 1.0 / sum;
                    double Preject_theo = Math.Pow(rho, n) / fact[n] * P0_theo;
                    double Q_theo = 1 - Preject_theo;
                    double A_theo = lambda * Q_theo;
                    double k_theo = rho * Q_theo;

                    double Preject_exp = (double)stats.Rejected / stats.Requests;
                    double Q_exp = (double)stats.Processed / stats.Requests;
                    double A_exp = stats.Processed / stats.TotalTime;
                    double k_exp = stats.AvgBusyChannels;
                    double P0_exp = stats.IdleProbability;

                    lambdaVals.Add(lambda);
                    expP0.Add(P0_exp);
                    theoP0.Add(P0_theo);
                    expPreject.Add(Preject_exp);
                    theoPreject.Add(Preject_theo);
                    expQ.Add(Q_exp);
                    theoQ.Add(Q_theo);
                    expA.Add(A_exp);
                    theoA.Add(A_theo);
                    expK.Add(k_exp);
                    theoK.Add(k_theo);

                    writer.WriteLine($"{lambda,-6:F2} {P0_exp,-8:F4} {P0_theo,-8:F4} {Preject_exp,-9:F4} {Preject_theo,-9:F4} {Q_exp,-7:F4} {Q_theo,-7:F4} {A_exp,-7:F2} {A_theo,-7:F2} {k_exp,-7:F3} {k_theo,-7:F3}");
                }
            }

            PlotAndSave(lambdaVals.ToArray(), expP0.ToArray(), theoP0.ToArray(), "Вероятность простоя P0", "result/p-1.png");
            PlotAndSave(lambdaVals.ToArray(), expPreject.ToArray(), theoPreject.ToArray(), "Вероятность отказа Pотк", "result/p-2.png");
            PlotAndSave(lambdaVals.ToArray(), expQ.ToArray(), theoQ.ToArray(), "Относительная пропускная способность Q", "result/p-3.png");
            PlotAndSave(lambdaVals.ToArray(), expA.ToArray(), theoA.ToArray(), "Абсолютная пропускная способность A", "result/p-4.png");
            PlotAndSave(lambdaVals.ToArray(), expK.ToArray(), theoK.ToArray(), "Среднее число занятых каналов k", "result/p-5.png");

            Console.WriteLine("\nРезультаты сохранены в data.txt");
            Console.WriteLine("Графики созданы в папке result");
        }

        struct ExperimentResult
        {
            public int Requests;
            public int Processed;
            public int Rejected;
            public double TotalTime;
            public double IdleProbability;
            public double AvgBusyChannels;
        }

        static ExperimentResult RunSingleExperiment(int n, double mu, double lambda, int totalRequests)
        {
            var server = new Server(n, mu);
            var client = new Client(server);
            var rand = new Random();

            DateTime startTime = DateTime.Now;

            for (int id = 1; id <= totalRequests; id++)
            {
                double interarrivalSec = -Math.Log(1 - rand.NextDouble()) / lambda;
                int sleepMs = (int)(interarrivalSec * 1000);
                if (sleepMs > 0) Thread.Sleep(sleepMs);
                client.Send(id);
            }

            while (true)
            {
                lock (server)
                {
                    if (server.RequestCount == server.ProcessedCount + server.RejectedCount)
                        break;
                }
                Thread.Sleep(10);
            }

            server.FinalizeTime();
            DateTime endTime = DateTime.Now;
            double totalTime = (endTime - startTime).TotalSeconds;

            return new ExperimentResult
            {
                Requests = server.RequestCount,
                Processed = server.ProcessedCount,
                Rejected = server.RejectedCount,
                TotalTime = totalTime,
                IdleProbability = server.IdleProbability,
                AvgBusyChannels = server.AvgBusyChannels
            };
        }

        static void PlotAndSave(double[] x, double[] yExp, double[] yTheo, string title, string filename)
        {
            var plt = new ScottPlot.Plot();
            var scatterExp = plt.Add.Scatter(x, yExp);
            scatterExp.LegendText = "Эксперимент";
            scatterExp.MarkerStyle.Shape = MarkerShape.FilledCircle;
            scatterExp.MarkerStyle.Size = 5;
            scatterExp.LineWidth = 1;

            var scatterTheo = plt.Add.Scatter(x, yTheo);
            scatterTheo.LegendText = "Теория";
            scatterTheo.MarkerStyle.Shape = MarkerShape.None;
            scatterTheo.LinePattern = LinePattern.Dashed;
            scatterTheo.LineWidth = 2;

            plt.Title(title);
            plt.XLabel("Интенсивность входного потока λ, заявок/с");
            plt.YLabel(title);
            plt.ShowLegend();
            plt.SavePng(filename, 800, 500);
        }
    }
}