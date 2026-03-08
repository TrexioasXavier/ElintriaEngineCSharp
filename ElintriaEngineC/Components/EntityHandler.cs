using System;
using System.Collections.Generic;
using System.Text;

namespace ElintriaEngineC.Components
{
    public  class EntityHandler
    {
        private static List<Entity> m_entities = new List<Entity>();

         public EntityHandler() 
        { 
        
        }
        static void AddEntity(Entity entity)
        {
            m_entities.Add(entity);
        }

        static void RemoveEntity(Entity entity)
        {
            m_entities.Remove(entity);
        }

        public static void Start()
        { 
        }

        public static void Update()
        { 
        }




















    }
}
