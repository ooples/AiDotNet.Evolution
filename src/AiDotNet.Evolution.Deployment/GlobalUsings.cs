extern alias AiDotNetConsumer;
global using IModelSerializer = AiDotNetConsumer::AiDotNet.Interfaces.IModelSerializer;
// AiDotNet 0.233 ships its own IProgramFitnessEvaluator in AiDotNet.Interfaces; this project means the Programs one.
global using IProgramFitnessEvaluator = AiDotNet.Evolution.Programs.IProgramFitnessEvaluator;
global using AiDotNetConsumer::AiDotNet.AutoML;
global using AiDotNetConsumer::AiDotNet.Models;
global using AiDotNetConsumer::AiDotNet.Interfaces;
